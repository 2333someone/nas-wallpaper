using NasWallpaper.Models;
using NasWallpaper.Services.Configuration;
using NasWallpaper.Services.Rotation;
using NasWallpaper.Services.Update;
using NasWallpaper.Services.Wallpaper;
using NasWallpaper.UI;
using System.IO;
using System.Windows;

namespace NasWallpaper;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private WindowsWallpaper? _wallpaper;
    private RotationEngine? _engine;
    private SettingsWindow? _settings;
    private readonly ConfigStore _store = new();
    private AppConfig _config = new();
    private bool _exiting;
    private UpdateInfo? _pendingUpdate;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 自动更新后新进程会带这个参数，等旧进程完全退出再抢单实例互斥体。
        var waitIndex = Array.IndexOf(e.Args, "--wait-for-exit");
        if (waitIndex >= 0 && waitIndex + 1 < e.Args.Length && int.TryParse(e.Args[waitIndex + 1], out var waitPid))
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(waitPid);
                previous.WaitForExit(15000);
            }
            catch (ArgumentException) { }
        }

        UpdateService.CleanupOldVersion();

        _mutex = CreateSingleInstanceMutex();
        if (_mutex is null) { Shutdown(); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _config = _store.Load();
        if (_store.LoadFailed)
            Log("配置文件读不出来，已暂停自动保存，等你在界面里重新保存一次。");
        else if (_store.RecoveredFromBackup)
            Log("配置文件损坏，已从备份恢复上一次的图片源设置。");
        ThemeManager.Apply(_config.Theme);
        try
        {
            _wallpaper = new WindowsWallpaper();
            _engine = new RotationEngine(_store, _config, _wallpaper);
            _engine.Changed += () => Dispatcher.Invoke(() =>
            {
                _tray?.UpdatePauseText(_config.Paused);
                if (_settings is { IsVisible: true }) _settings.RefreshStatuses();
            });
            _tray = new TrayIcon();
            _tray.OpenRequested += ShowSettings;
            _tray.NextRequested += () => _engine.NextAsync();
            _tray.PauseRequested += async () => { await TogglePause(); };
            _tray.ExitRequested += ExitApp;
            _tray.UpdatePauseText(_config.Paused);
            _engine.Start();
            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) ShowSettings();
            _ = CheckUpdatesOnStartupAsync();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            Shutdown();
        }
    }

    /// <summary>优先用 Global 单实例：本地和远程桌面各开一个时也只允许一个进程写配置。</summary>
    private static Mutex? CreateSingleInstanceMutex()
    {
        try
        {
            var global = new Mutex(true, @"Global\NAS Wallpaper Single Instance", out var created);
            if (created) return global;
            global.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var local = new Mutex(true, @"Local\NAS Wallpaper Single Instance", out var created);
            return created ? local : null;
        }
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_store.RootDirectory);
            File.AppendAllText(Path.Combine(_store.RootDirectory, "error.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }


    private void ShowSettings()
    {
        if (_engine is not null)
        {
            try
            {
                if (_engine.RefreshDisplaysNow()) _config = _engine.Snapshot;
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.LoadConfig(_config, _engine?.GetStatuses(), _store.DefaultCacheDirectory);
            _settings.ShowPendingUpdate(_pendingUpdate);
            _settings.SaveRequested += SaveConfig;
            _settings.NextRequested += () => _engine?.NextAsync() ?? Task.CompletedTask;
            _settings.PreviousRequested += () => _engine?.PreviousAsync() ?? Task.CompletedTask;
            _settings.PauseRequested += TogglePause;
            _settings.StatusRequested += () => _engine?.GetStatuses() ?? [];
            _settings.CheckUpdateRequested += CheckUpdateAsync;
            _settings.InstallUpdateRequested += InstallUpdateAsync;
            _settings.DeleteImageRequested += (monitorId, path) =>
                _engine?.DeleteImageAsync(monitorId, path) ?? Task.FromResult(false);
            _settings.ImagesRequested += (monitorId, refresh) => _engine is null
                ? Task.FromResult<IReadOnlyList<SourceFile>>([])
                : _engine.GetImagesAsync(monitorId, refresh, CancellationToken.None);
            _settings.ThumbnailLoaderRequested += monitorId => _engine?.CreateThumbnailLoader(monitorId);
            _settings.ApplyImageRequested += (monitorId, file) => _engine is null
                ? Task.FromResult(false)
                : _engine.ApplyImageAsync(monitorId, file);
            _settings.ImportImageRequested += (monitorId, path) => _engine is null
                ? Task.FromResult(new ImportImageResult(false, "壁纸引擎尚未启动。"))
                : _engine.ImportImageAsync(monitorId, path);
            _settings.ExitRequested += ExitApp;
        }
        if (_settings.IsVisible) { _settings.Activate(); return; }
        _settings.LoadConfig(_config, _engine?.GetStatuses(), _store.DefaultCacheDirectory);
        _settings.ShowPendingUpdate(_pendingUpdate);
        _settings.Show();
        _settings.Activate();
    }

    private async Task<UpdateInfo?> CheckUpdateAsync(string repository)
    {
        _pendingUpdate = await UpdateService.CheckAsync(repository, CancellationToken.None);
        return _pendingUpdate;
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            if (!_config.CheckUpdates) return;
            if (!UpdateService.TryNormalizeRepository(_config.UpdateRepository, out var repository)) return;

            var info = await UpdateService.CheckAsync(repository, CancellationToken.None);
            if (info is null) return;
            _pendingUpdate = info;
            Dispatcher.Invoke(() => _settings?.ShowPendingUpdate(info));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
            or IOException or TaskCanceledException)
        {
            // 自动检查失败时保持安静，不打扰使用。
        }
    }

    private async Task<bool> InstallUpdateAsync(UpdateInfo info, IProgress<double> progress)
    {
        string? download = null;
        try
        {
            download = Path.Combine(Path.GetTempPath(), $"NAS-Wallpaper-{info.Version}.exe");
            await UpdateService.DownloadAsync(info, download, progress, CancellationToken.None);
            _exiting = true;
            if (_engine is not null) await _engine.DisposeAsync();
            UpdateService.ApplyAndRestart(download);
            Shutdown();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            if (download is not null)
            {
                try { File.Delete(download); } catch (IOException) { }
            }
            return false;
        }
    }

    private async Task SaveConfig(AppConfig config)
    {
        if (config.StartWithWindows != _config.StartWithWindows)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (config.StartWithWindows) key?.SetValue("NAS Wallpaper", '"' + Environment.ProcessPath + '"');
            else key?.DeleteValue("NAS Wallpaper", false);
        }
        _store.SetCacheDirectory(config.CacheDirectory);
        if (_engine is not null) await _engine.ApplyConfigAsync(config);
        else { _config = config; _store.Save(config, force: true); }
        _config = _engine?.Snapshot ?? config;
        _settings?.LoadConfig(_config, _engine?.GetStatuses(), _store.DefaultCacheDirectory);
        _tray?.UpdatePauseText(_config.Paused);
    }

    private async Task<bool> TogglePause()
    {
        if (_engine is not null)
        {
            await _engine.TogglePauseAsync();
            _config = _engine.Snapshot;
        }
        _tray?.UpdatePauseText(_config.Paused);
        return _config.Paused;
    }

    private async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        if (_engine is not null) await _engine.DisposeAsync();
        Shutdown();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _tray?.Dispose();
        _wallpaper?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
