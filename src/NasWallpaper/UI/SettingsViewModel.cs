using NasWallpaper.Models;
using NasWallpaper.Services.Update;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NasWallpaper.UI;

public sealed class SettingsViewModel : ObservableObject
{
    private string _message = "尚未保存的配置不会影响正在运行的轮播。";
    private bool _messageIsError;

    public AppConfig Config { get; private set; } = new();
    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public string Message { get => _message; set { if (SetField(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool MessageIsError { get => _messageIsError; set => SetField(ref _messageIsError, value); }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public string PauseText => Config.Paused ? "继续轮播" : "暂停轮播";
    public string PauseGlyph => Config.Paused ? "\uE768" : "\uE769";
    public string StatusText => Config.Paused ? "轮播已暂停" : "后台轮播已就绪";
    public string StatusDetail => Config.Paused
        ? "桌面将保持当前画面，随时可以继续。"
        : "优先直接使用 NAS 原图，断网时由本地缓存兜底。";

    /// <summary>侧边栏用的单行提示（空间窄，不做换行）。</summary>
    public string SideHint => Config.Paused ? "桌面保持当前画面。" : "断网时继续使用本地缓存。";

    public int CacheLimitMb { get => Config.CacheLimitMb; set { Config.CacheLimitMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(CacheSummary)); } }
    public int CacheRetentionDays { get => Config.CacheRetentionDays; set { Config.CacheRetentionDays = value; OnPropertyChanged(); } }
    public int ScanIntervalMinutes { get => Config.ScanIntervalMinutes; set { Config.ScanIntervalMinutes = value; OnPropertyChanged(); } }
    public bool StartWithWindows { get => Config.StartWithWindows; set { Config.StartWithWindows = value; OnPropertyChanged(); } }
    public int StyleIndex { get => (int)Config.Style; set { Config.Style = (WallpaperStyle)value; OnPropertyChanged(); OnPropertyChanged(nameof(StyleName)); } }
    public int ThemeIndex
    {
        get => (int)Config.Theme;
        set
        {
            if ((int)Config.Theme == value) return;
            Config.Theme = (AppTheme)value;
            OnPropertyChanged();
            ThemeManager.Apply(Config.Theme);
        }
    }

    public int MonitorCount => Monitors.Count;
    public int EnabledCount => Monitors.Count(m => m.Enabled);
    public bool HasMonitors => Monitors.Count > 0;
    public string CacheSummary => CacheLimitMb >= 1024 ? $"{CacheLimitMb / 1024d:0.#} GB" : $"{CacheLimitMb} MB";
    public string StyleName => StyleIndex switch
    {
        0 => "填充", 1 => "适应", 2 => "拉伸", 3 => "平铺", 4 => "居中", _ => "跨屏"
    };
    public string Version => UpdateService.CurrentVersion.ToString(3);

    public string UpdateRepository
    {
        get => Config.UpdateRepository;
        set { if (Config.UpdateRepository == value) return; Config.UpdateRepository = value; OnPropertyChanged(); }
    }

    public bool CheckUpdates
    {
        get => Config.CheckUpdates;
        set { if (Config.CheckUpdates == value) return; Config.CheckUpdates = value; OnPropertyChanged(); }
    }

    private string _updateStatus = "尚未检查更新。";
    public string UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }

    private string _updateNotes = "";
    public string UpdateNotes { get => _updateNotes; private set { if (SetField(ref _updateNotes, value)) OnPropertyChanged(nameof(HasUpdateNotes)); } }
    public bool HasUpdateNotes => !string.IsNullOrWhiteSpace(UpdateNotes);

    private bool _updateAvailable;
    public bool UpdateAvailable { get => _updateAvailable; private set => SetField(ref _updateAvailable, value); }

    public UpdateInfo? PendingUpdate { get; private set; }

    public void BeginUpdateCheck()
    {
        UpdateAvailable = false;
        UpdateStatus = "正在检查新版本…";
    }

    public void ApplyUpdateInfo(UpdateInfo? info)
    {
        PendingUpdate = info;
        UpdateAvailable = info is not null;
        UpdateNotes = info?.Notes ?? "";
        UpdateStatus = info is null
            ? $"已是最新版本（v{Version}）。"
            : $"发现新版本 v{info.Version}，当前 v{Version}。";
    }

    public void SetUpdateError(string message)
    {
        UpdateAvailable = false;
        UpdateStatus = message;
    }

    public void SetUpdateProgress(double percent) =>
        UpdateStatus = $"正在下载新版本… {percent:0}%";

    private string _defaultCachePath = "";
    public string DefaultCachePath
    {
        get => _defaultCachePath;
        set
        {
            if (!SetField(ref _defaultCachePath, value)) return;
            OnPropertyChanged(nameof(CachePathText));
            OnPropertyChanged(nameof(CacheLocationHint));
        }
    }

    public bool IsCustomCache => !string.IsNullOrWhiteSpace(Config.CacheDirectory);
    public string CachePathText => IsCustomCache ? Config.CacheDirectory : DefaultCachePath;
    public string CacheLocationHint => IsCustomCache
        ? "自定义位置：图片会缓存到这里，旧目录中的文件不会自动删除。"
        : "默认位置：系统盘用户目录，第三方清理工具可能会清空它。";

    /// <summary>切换缓存目录；传入空值恢复默认位置（只改配置，保存后生效）。</summary>
    public void SetCacheDirectory(string? path)
    {
        Config.CacheDirectory = string.IsNullOrWhiteSpace(path) ? "" : path;
        OnPropertyChanged(nameof(CachePathText));
        OnPropertyChanged(nameof(IsCustomCache));
        OnPropertyChanged(nameof(CacheLocationHint));
    }

    public void Load(AppConfig config, IReadOnlyList<MonitorStatus>? statuses = null, string? defaultCachePath = null)
    {
        if (!string.IsNullOrWhiteSpace(defaultCachePath)) DefaultCachePath = defaultCachePath;

        if (CanReuseMonitors(config))
        {
            // 显示器没变时原地更新，避免重建控件导致开关“闪一下”。
            CopySettings(config);
            for (var i = 0; i < Monitors.Count; i++) Monitors[i].UpdateFrom(config.Monitors[i]);
        }
        else
        {
            Config = config.Clone();
            Monitors.Clear();
            foreach (var monitor in Config.Monitors) Monitors.Add(new MonitorViewModel(monitor));
        }

        if (statuses is not null) ApplyStatuses(statuses);
        OnPropertyChanged(nameof(Config)); OnPropertyChanged(nameof(PauseText)); OnPropertyChanged(nameof(PauseGlyph));
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusDetail)); OnPropertyChanged(nameof(SideHint));
        OnPropertyChanged(nameof(CacheLimitMb)); OnPropertyChanged(nameof(CacheRetentionDays));
        OnPropertyChanged(nameof(ScanIntervalMinutes)); OnPropertyChanged(nameof(StartWithWindows)); OnPropertyChanged(nameof(StyleIndex));
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(MonitorCount)); OnPropertyChanged(nameof(EnabledCount)); OnPropertyChanged(nameof(HasMonitors));
        OnPropertyChanged(nameof(CacheSummary)); OnPropertyChanged(nameof(StyleName));
        OnPropertyChanged(nameof(CachePathText)); OnPropertyChanged(nameof(IsCustomCache));
        OnPropertyChanged(nameof(CacheLocationHint));
    }

    private bool CanReuseMonitors(AppConfig config) =>
        config.Monitors.Count == Monitors.Count &&
        config.Monitors.Select(m => m.MonitorId)
            .SequenceEqual(Monitors.Select(m => m.Model.MonitorId), StringComparer.Ordinal);

    private void CopySettings(AppConfig source)
    {
        Config.Paused = source.Paused;
        Config.StartWithWindows = source.StartWithWindows;
        Config.Style = source.Style;
        Config.Theme = source.Theme;
        Config.CacheLimitMb = source.CacheLimitMb;
        Config.CacheRetentionDays = source.CacheRetentionDays;
        Config.ScanIntervalMinutes = source.ScanIntervalMinutes;
        Config.CacheDirectory = source.CacheDirectory;
        Config.UpdateRepository = source.UpdateRepository;
        Config.CheckUpdates = source.CheckUpdates;
    }

    public void ApplyStatuses(IReadOnlyList<MonitorStatus> statuses)
    {
        foreach (var monitor in Monitors)
        {
            var status = statuses.FirstOrDefault(s => s.MonitorId == monitor.Model.MonitorId);
            if (status is not null) monitor.ApplyStatus(status);
        }
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(EnabledCount));
    }

    /// <summary>把暂停状态写回本地视图模型，并刷新由它推导出的展示字段。</summary>
    public void SetPaused(bool paused)
    {
        Config.Paused = paused;
        RefreshRuntimeText();
    }

    /// <summary>刷新由运行状态推导出的展示字段（暂停/继续后调用）。</summary>
    public void RefreshRuntimeText()
    {
        OnPropertyChanged(nameof(PauseText)); OnPropertyChanged(nameof(PauseGlyph));
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusDetail)); OnPropertyChanged(nameof(SideHint));
    }

    public bool Validate(out string error)
    {
        if (CacheLimitMb is < 128 or > 10240 || CacheRetentionDays is < 1 or > 365 || ScanIntervalMinutes is < 5 or > 1440)
        { error = "请检查缓存与刷新设置的有效范围。"; return false; }
        if (IsCustomCache && !IsValidCacheDirectory(Config.CacheDirectory))
        { error = "缓存位置无效，请重新选择文件夹。"; return false; }
        foreach (var p in Monitors.Where(p => p.Enabled))
        {
            if (p.Source.Kind == SourceKind.Smb && !p.Source.Address.StartsWith(@"\\", StringComparison.Ordinal))
            { error = $"{p.Name} 的 SMB 路径必须以 \\\\ 开头。"; return false; }
            if (p.Source.Kind == SourceKind.WebDav &&
                (!Uri.TryCreate(p.Source.Address, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            { error = $"{p.Name} 的 WebDAV 地址必须是有效的 HTTP(S) URL。"; return false; }
            if (string.IsNullOrWhiteSpace(p.Source.Address)) { error = $"请填写 {p.Name} 的图片目录。"; return false; }
        }
        error = ""; return true;
    }

    private static bool IsValidCacheDirectory(string path)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path.Trim());
            return !string.Equals(fullPath, System.IO.Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return false;
        }
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
}
