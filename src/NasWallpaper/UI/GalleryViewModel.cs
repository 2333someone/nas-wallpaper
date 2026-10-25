using NasWallpaper.Models;
using NasWallpaper.Services.Preview;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace NasWallpaper.UI;

/// <summary>壁纸库页面的状态：当前显示器、已加载的缩略图、分页信息。</summary>
public sealed class GalleryViewModel : ObservableObject
{
    private MonitorViewModel? _selectedMonitor;
    private bool _isLoading;
    private string _summary = "选择一台显示器，浏览它的图片源。";
    private bool _canLoadMore;

    public ObservableCollection<GalleryImage> Images { get; } = [];

    public MonitorViewModel? SelectedMonitor
    {
        get => _selectedMonitor;
        set => SetField(ref _selectedMonitor, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (!SetField(ref _isLoading, value)) return;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public string Summary { get => _summary; set => SetField(ref _summary, value); }
    public bool CanLoadMore { get => _canLoadMore; set => SetField(ref _canLoadMore, value); }
    public bool IsEmpty => !IsLoading && Images.Count == 0;

    /// <summary>列表增删后刷新由数量推导出来的展示字段。</summary>
    public void NotifyCountChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Images));
    }

    public void Reset(string summary)
    {
        Images.Clear();
        CanLoadMore = false;
        Summary = summary;
        NotifyCountChanged();
    }
}

/// <summary>壁纸库里的一张图片；缩略图由后台异步加载。</summary>
public sealed class GalleryImage(SourceFile file) : ObservableObject
{
    private BitmapSource? _thumbnail;
    private bool _failed;
    private int _sourceWidth;
    private int _sourceHeight;

    public SourceFile File { get; } = file;
    public string Name => Path.GetFileName(File.Location);

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!SetField(ref _thumbnail, value)) return;
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool Failed
    {
        get => _failed;
        set
        {
            if (!SetField(ref _failed, value)) return;
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>把加载结果写进卡片；尺寸用原图分辨率，而不是缩略图分辨率。</summary>
    public void ApplyPreview(PreviewImage? preview)
    {
        Thumbnail = preview?.Image;
        _sourceWidth = preview?.Width ?? 0;
        _sourceHeight = preview?.Height ?? 0;
        Failed = preview is null;
        OnPropertyChanged(nameof(DetailText));
    }

    public string DetailText
    {
        get
        {
            var size = File.Length >= 1024 * 1024
                ? $"{File.Length / 1024d / 1024d:0.#} MB"
                : $"{Math.Max(1, File.Length / 1024)} KB";
            if (_sourceWidth > 0 && _sourceHeight > 0)
                return $"{size} · {_sourceWidth}×{_sourceHeight}";
            return _failed ? "无法预览" : size;
        }
    }
}
