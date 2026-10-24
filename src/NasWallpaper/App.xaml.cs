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

        _mutex = new Mutex(true, "Local\\NAS Wallpaper Single Instance", out var created);
        if (!created) { Shutdown(); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _config = _store.Load();
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
            try
            {
                Directory.CreateDirectory(_store.CacheDirectory);
                File.AppendAllText(Path.Combine(_store.CacheDirectory, "error.log"), ex + Environment.NewLine);
            }
            catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException) { }
            Shutdown();
        }
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
        else { _config = config; _store.Save(config); }
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
