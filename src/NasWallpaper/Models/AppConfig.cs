using System.Text.Json.Serialization;

namespace NasWallpaper.Models;

public enum SourceKind { Smb, WebDav, Local }
public enum PlaybackMode { Sequential, Random }
public enum WallpaperStyle { Fill = 0, Fit = 1, Stretch = 2, Tile = 3, Center = 4, Span = 5 }
public enum AppTheme { System = 0, Dark = 1, Light = 2 }
public enum WallpaperOrientation { Any = 0, Landscape = 1 }

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public bool Paused { get; set; }
    public bool StartWithWindows { get; set; }
    public WallpaperStyle Style { get; set; } = WallpaperStyle.Fill;

    /// <summary>界面主题：跟随系统 / 深色 / 浅色。</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;
    public int CacheLimitMb { get; set; } = 1024;
    public int CacheRetentionDays { get; set; } = 14;
    public int ScanIntervalMinutes { get; set; } = 60;

    /// <summary>自定义缓存目录；为空表示使用默认位置（用户目录下的 NAS Wallpaper\Cache）。</summary>
    public string CacheDirectory { get; set; } = "";

    /// <summary>GitHub 仓库（owner/repo），留空则关闭在线更新。</summary>
    public string UpdateRepository { get; set; } = "2333someone/nas-wallpaper";

    /// <summary>启动时自动检查新版本。</summary>
    public bool CheckUpdates { get; set; } = true;
    public List<MonitorConfig> Monitors { get; set; } = [];

    public AppConfig Clone() => JsonConfig.Clone(this);
}

public sealed class MonitorConfig
{
    public string MonitorId { get; set; } = "";
    public string Name { get; set; } = "显示器";
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 30;
    public PlaybackMode Playback { get; set; } = PlaybackMode.Random;

    /// <summary>是否把竖向壁纸也纳入轮播；关闭时只轮播横向壁纸。</summary>
    public bool IncludePortrait { get; set; }

    [JsonIgnore]
    public WallpaperOrientation Orientation =>
        IncludePortrait ? WallpaperOrientation.Any : WallpaperOrientation.Landscape;

    public string? LocalDefaultPath { get; set; }
    public SourceConfig Source { get; set; } = new();
    public string? LastGoodPath { get; set; }
    public string? LastWallpaperPath { get; set; }
}

public sealed class SourceConfig : System.ComponentModel.INotifyPropertyChanged
{
    private SourceKind _kind = SourceKind.Smb;

    public SourceKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            PropertyChanged?.Invoke(this, new(nameof(Kind)));
            PropertyChanged?.Invoke(this, new(nameof(KindIndex)));
        }
    }

    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public bool Recursive { get; set; }

    /// <summary>WebDAV 使用自签名证书时跳过 TLS 校验（仅用于自己的 NAS）。</summary>
    public bool AllowInvalidCertificate { get; set; }

    [JsonIgnore]
    public int KindIndex { get => (int)Kind; set => Kind = (SourceKind)value; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record SourceFile(string Location, long Length, DateTimeOffset LastWriteUtc);

public sealed record CachedImage(string Path, string Key, long Length, DateTimeOffset LastWriteUtc, string Name = "");

public sealed record DisplayInfo(string Id, string Name, int Width, int Height, bool IsPrimary);

/// <summary>拖拽导入图片的结果：成功与否，以及给用户看的一句话。</summary>
public sealed record ImportImageResult(bool Success, string Message);

public sealed record MonitorStatus(
    string MonitorId,
    string Message,
    string? CurrentImage,
    DateTimeOffset? NextChangeUtc,
    bool IsBusy = false,
    string? CurrentImagePath = null);

internal static class JsonConfig
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Clone<T>(T value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(
            System.Text.Json.JsonSerializer.Serialize(value, Options), Options)!;
}
