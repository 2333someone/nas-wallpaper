using NasWallpaper.Models;
using System.IO;
using System.Text.Json;

namespace NasWallpaper.Services.Configuration;

public sealed class ConfigStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public ConfigStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NAS Wallpaper");
        Directory.CreateDirectory(root);
        RootDirectory = root;
        _path = Path.Combine(root, "settings.json");
        CacheDirectory = DefaultCacheDirectory;
    }

    /// <summary>配置文件所在目录。</summary>
    public string RootDirectory { get; }

    /// <summary>默认缓存目录：配置文件目录下的 Cache。</summary>
    public string DefaultCacheDirectory => Path.Combine(RootDirectory, "Cache");

    /// <summary>当前生效的缓存目录，可在运行时切换。</summary>
    public string CacheDirectory { get; private set; }

    public bool IsDefaultCacheDirectory =>
        string.Equals(CacheDirectory, DefaultCacheDirectory, StringComparison.OrdinalIgnoreCase);

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                CacheDirectory = DefaultCacheDirectory;
                return new AppConfig();
            }
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), JsonConfig.Options) ?? new();
            Normalize(config);
            CacheDirectory = ResolveCacheDirectory(config.CacheDirectory);
            config.CacheDirectory = IsDefaultCacheDirectory ? "" : CacheDirectory;
            return config;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CacheDirectory = DefaultCacheDirectory;
            return new AppConfig();
        }
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

    public void Save(AppConfig config)
    {
        _saveLock.Wait();
        try
        {
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonConfig.Options));
            File.Move(temp, _path, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static void Normalize(AppConfig config)
    {
        config.CacheLimitMb = Math.Clamp(config.CacheLimitMb, 128, 10240);
        config.CacheRetentionDays = Math.Clamp(config.CacheRetentionDays, 1, 365);
        config.ScanIntervalMinutes = Math.Clamp(config.ScanIntervalMinutes, 5, 1440);
        foreach (var monitor in config.Monitors)
        {
            monitor.IntervalMinutes = Math.Clamp(monitor.IntervalMinutes, 1, 1440);
            monitor.Source ??= new();
        }
    }
}
