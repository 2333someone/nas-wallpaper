using NasWallpaper.Models;
using System.IO;
using System.Text.Json;

namespace NasWallpaper.Services.Configuration;

public sealed class ConfigStore
{
    private readonly string _path;
    private readonly string _backupPath;
    private readonly string _lastGoodPath;
    private readonly string _corruptPath;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public ConfigStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NAS Wallpaper");
        Directory.CreateDirectory(root);
        RootDirectory = root;
        _path = Path.Combine(root, "settings.json");
        _backupPath = _path + ".bak";
        _lastGoodPath = Path.Combine(root, "settings.lastgood.json");
        _corruptPath = _path + ".corrupt";
        CacheDirectory = DefaultCacheDirectory;
    }

    /// <summary>配置文件所在目录。</summary>
    public string RootDirectory { get; }

    /// <summary>默认缓存目录：配置文件目录下的 Cache。</summary>
    public string DefaultCacheDirectory => Path.Combine(RootDirectory, "Cache");

    /// <summary>当前生效的缓存目录，可在运行时切换。</summary>
    public string CacheDirectory { get; private set; }

    /// <summary>上次启动时主配置读不出来，且备份也不可用；此时禁止自动保存覆盖现场。</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>本次配置是从备份恢复的，建议在日志里留一条记录。</summary>
    public bool RecoveredFromBackup { get; private set; }

    public bool IsDefaultCacheDirectory =>
        string.Equals(CacheDirectory, DefaultCacheDirectory, StringComparison.OrdinalIgnoreCase);

    public AppConfig Load()
    {
        LoadFailed = false;
        RecoveredFromBackup = false;

        if (!File.Exists(_path))
        {
            CacheDirectory = DefaultCacheDirectory;
            return new AppConfig();
        }

        if (TryRead(_path, out var config, out var json))
        {
            RememberLastGood(json, config);
            return Prepare(config);
        }

        // 主文件坏了（写盘中断、被杀毒锁住、手工改错）：先找最后一次配好图片源的版本，再找上一版。
        foreach (var candidate in new[] { _lastGoodPath, _backupPath })
        {
            if (!TryRead(candidate, out config, out json)) continue;
            RecoveredFromBackup = true;
            PreserveCorruptFile();
            try { WriteAtomic(_path, json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return Prepare(config);
        }

        LoadFailed = true;
        PreserveCorruptFile();
        CacheDirectory = DefaultCacheDirectory;
        return new AppConfig();
    }

    /// <summary>切换缓存目录；传入空值会恢复默认位置。</summary>
    public string SetCacheDirectory(string? directory)
    {
        CacheDirectory = ResolveCacheDirectory(directory);
        return CacheDirectory;
    }

    private string ResolveCacheDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return DefaultCacheDirectory;
        try
        {
            var fullPath = Path.GetFullPath(directory.Trim());
            if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
                return DefaultCacheDirectory;
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DefaultCacheDirectory;
        }
    }

    /// <param name="force">
    /// 用户主动点保存时为 true，即使当前读配置失败也照写；
    /// 后台轮播的自动保存传 false，避免用空配置把坏掉的现场覆盖掉。
    /// </param>
    public void Save(AppConfig config, bool force = false)
    {
        _saveLock.Wait();
        try
        {
            var hasSource = HasBoundSource(config);
            if (LoadFailed && !hasSource && !force) return;

            var json = JsonSerializer.Serialize(config, JsonConfig.Options);
            if (File.Exists(_path))
            {
                try { File.Copy(_path, _backupPath, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            WriteAtomic(_path, json);
            LoadFailed = false;

            // 只有带图片源的配置才值得当“最后一次好配置”，空配置不准覆盖它。
            if (hasSource)
            {
                try { WriteAtomic(_lastGoodPath, json); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private AppConfig Prepare(AppConfig config)
    {
        Normalize(config);
        CacheDirectory = ResolveCacheDirectory(config.CacheDirectory);
        config.CacheDirectory = IsDefaultCacheDirectory ? "" : CacheDirectory;
        return config;
    }

    private static bool TryRead(string path, out AppConfig config, out string json)
    {
        config = new AppConfig();
        json = "";
        try
        {
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parsed = JsonSerializer.Deserialize<AppConfig>(text, JsonConfig.Options);
            if (parsed is null) return false;
            config = parsed;
            json = text;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>成功读到一份带图片源的配置时留一份底，丢失后可以直接靠它恢复。</summary>
    private void RememberLastGood(string json, AppConfig config)
    {
        if (!HasBoundSource(config)) return;
        try
        {
            if (File.Exists(_lastGoodPath) && File.ReadAllText(_lastGoodPath) == json) return;
            WriteAtomic(_lastGoodPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>把坏掉的主文件另存一份，方便排查；不删除原文件，避免下次启动误判成“首次运行”。</summary>
    private void PreserveCorruptFile()
    {
        try
        {
            if (File.Exists(_path)) File.Copy(_path, _corruptPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool HasBoundSource(AppConfig config) =>
        config.Monitors is not null &&
        config.Monitors.Any(m => !string.IsNullOrWhiteSpace(m?.Source?.Address));

    private static void WriteAtomic(string path, string json)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, true);
    }

    private static void Normalize(AppConfig config)
    {
        config.CacheLimitMb = Math.Clamp(config.CacheLimitMb, 128, 10240);
        config.CacheRetentionDays = Math.Clamp(config.CacheRetentionDays, 1, 365);
        config.ScanIntervalMinutes = Math.Clamp(config.ScanIntervalMinutes, 5, 1440);
        if (config.Monitors is null) config.Monitors = [];
        config.Monitors.RemoveAll(m => m is null);
        foreach (var monitor in config.Monitors)
        {
            monitor.IntervalMinutes = Math.Clamp(monitor.IntervalMinutes, 1, 1440);
            monitor.Source ??= new();
        }
    }
}
