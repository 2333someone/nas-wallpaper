using NasWallpaper.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NasWallpaper.UI;

public sealed class MonitorViewModel(MonitorConfig model) : INotifyPropertyChanged
{
    private string _status = "等待首次同步";
    private string _currentImageName = "";
    private string? _currentImagePath;
    private string? _pendingDeletePath;
    private bool _deletePending;
    private string? _nextChangeText;
    private bool _isBusy;

    public MonitorConfig Model { get; } = model;
    public SourceConfig Source => Model.Source;
    public string Name => Model.Name;
    public string? LocalDefaultPath { get => Model.LocalDefaultPath; set { Model.LocalDefaultPath = value; OnPropertyChanged(); } }
    public bool Enabled { get => Model.Enabled; set { Model.Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(EnabledText)); } }
    public bool IncludePortrait
    {
        get => Model.IncludePortrait;
        set
        {
            if (Model.IncludePortrait == value) return;
            Model.IncludePortrait = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OrientationText));
        }
    }

    public int IntervalMinutes { get => Model.IntervalMinutes; set { Model.IntervalMinutes = value; OnPropertyChanged(); OnPropertyChanged(nameof(IntervalText)); } }
    public int PlaybackIndex { get => (int)Model.Playback; set { Model.Playback = (PlaybackMode)value; OnPropertyChanged(); } }

    public string EnabledText => Enabled ? "轮播中" : "已停用";
    public string OrientationText => IncludePortrait ? "横向 + 竖向" : "仅横向";
    public string IntervalText => IntervalMinutes switch
    {
        < 60 => $"{IntervalMinutes} 分钟",
        _ => $"{IntervalMinutes / 60d:0.#} 小时"
    };

    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string CurrentImageName { get => _currentImageName; private set { if (SetField(ref _currentImageName, value)) { OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(CanDeleteImage)); } } }

    public string? CurrentImagePath
    {
        get => _currentImagePath;
        private set { if (SetField(ref _currentImagePath, value)) OnPropertyChanged(nameof(CanDeleteImage)); }
    }

    /// <summary>缓存目录里的真实文件才允许删除；内置保底图与空状态不允许。</summary>
    public bool CanDeleteImage => !string.IsNullOrEmpty(CurrentImagePath) &&
        !string.Equals(CurrentImageName, "default.png", StringComparison.OrdinalIgnoreCase);

    public bool DeletePending
    {
        get => _deletePending;
        private set { if (SetField(ref _deletePending, value)) OnPropertyChanged(nameof(DeleteText)); }
    }

    public string DeleteText => DeletePending ? "再点一次删除" : "删除";

    /// <summary>第一次点击时记下当时显示的那张图，之后即使换了壁纸也删这张。</summary>
    public void BeginDelete()
    {
        if (!CanDeleteImage) return;
        _pendingDeletePath = CurrentImagePath;
        DeletePending = true;
    }

    public void CancelDelete()
    {
        _pendingDeletePath = null;
        DeletePending = false;
    }

    /// <summary>取出待删除的目标文件，并结束确认状态。</summary>
    public string? TakeDeleteTarget()
    {
        var path = _pendingDeletePath;
        _pendingDeletePath = null;
        DeletePending = false;
        return path;
    }
    public string? NextChangeText { get => _nextChangeText; private set => SetField(ref _nextChangeText, value); }
    public bool HasImage => !string.IsNullOrEmpty(CurrentImageName);
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }

    /// <summary>把新配置写回现有对象，界面绑定不重建，避免保存时开关重建闪动。</summary>
    public void UpdateFrom(MonitorConfig source)
    {
        if (Model.Name != source.Name) { Model.Name = source.Name; OnPropertyChanged(nameof(Name)); }
        if (Model.Enabled != source.Enabled)
        {
            Model.Enabled = source.Enabled;
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(EnabledText));
        }
        if (Model.IntervalMinutes != source.IntervalMinutes)
        {
            Model.IntervalMinutes = source.IntervalMinutes;
            OnPropertyChanged(nameof(IntervalMinutes));
            OnPropertyChanged(nameof(IntervalText));
        }
        if (Model.Playback != source.Playback)
        {
            Model.Playback = source.Playback;
            OnPropertyChanged(nameof(PlaybackIndex));
        }
        if (Model.IncludePortrait != source.IncludePortrait)
        {
            Model.IncludePortrait = source.IncludePortrait;
            OnPropertyChanged(nameof(IncludePortrait));
            OnPropertyChanged(nameof(OrientationText));
        }
        Model.LastGoodPath = source.LastGoodPath;
        Model.LastWallpaperPath = source.LastWallpaperPath;
        Model.LocalDefaultPath = source.LocalDefaultPath;

        var target = Model.Source ??= new SourceConfig();
        var incoming = source.Source ?? new SourceConfig();
        target.Kind = incoming.Kind;
        target.Address = incoming.Address;
        target.Username = incoming.Username;
        target.EncryptedPassword = incoming.EncryptedPassword;
        target.Recursive = incoming.Recursive;
        target.AllowInvalidCertificate = incoming.AllowInvalidCertificate;
    }

    public void ApplyStatus(MonitorStatus status)
    {
        Status = status.Message;
        CurrentImageName = status.CurrentImage ?? "";
        CurrentImagePath = status.CurrentImagePath;
        IsBusy = status.IsBusy;
        NextChangeText = status.NextChangeUtc is { } next
            ? next.ToLocalTime().ToString("HH:mm") + " 换图"
            : null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
}
