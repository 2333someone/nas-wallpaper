using NasWallpaper.Models;
using NasWallpaper.Services.Configuration;
using NasWallpaper.Services.Preview;
using NasWallpaper.Services.Sources;
using NasWallpaper.Services.Update;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace NasWallpaper.UI;

public partial class SettingsWindow : Window
{
    private const int GalleryIndex = 1;
    private const int GalleryPageSize = 60;
    private const int GalleryMaxItems = 480;
    private const int ThumbnailWidth = 240;
    private const int ViewerImageWidth = 2560;

    private static readonly string[] PageTitles = ["概览", "壁纸库", "显示器与图片源", "缓存与系统", "关于"];
    private static readonly string[] PageSubtitles =
    [
        "所有显示器都在后台安静地工作。",
        "浏览图片源里的每一张图，看中哪张就让它上桌面。",
        "为每台显示器指定图片目录，以及它自己的节奏。",
        "缓存策略、桌面外观与开机启动。",
        "轻量、本地、只连接你自己的图片源。"
    ];

    private readonly SettingsViewModel _viewModel;
    private readonly StackPanel[] _pages;
    private readonly RadioButton[] _navButtons;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly List<SourceFile> _galleryFiles = [];
    private readonly SemaphoreSlim _thumbnailGate = new(2, 2);
    private CancellationTokenSource? _galleryCts;
    private GalleryThumbnailLoader? _galleryLoader;
    private ImageViewerWindow? _viewer;
    private int _galleryShown;
    private bool _galleryLoaded;
    private int _pageIndex = -1;
    private bool _closingToTray = true;
    private UIElement? _pendingDeleteButton;

    public event Func<AppConfig, Task>? SaveRequested;
    public event Func<Task>? NextRequested;
    public event Func<Task>? PreviousRequested;
    public event Func<Task<bool>>? PauseRequested;
    public event Func<IReadOnlyList<MonitorStatus>>? StatusRequested;
    public event Func<string, string?, Task<bool>>? DeleteImageRequested;
    public event Func<string, Task<UpdateInfo?>>? CheckUpdateRequested;
    public event Func<UpdateInfo, IProgress<double>, Task<bool>>? InstallUpdateRequested;
    public event Func<string, bool, Task<IReadOnlyList<SourceFile>>>? ImagesRequested;
    public event Func<string, GalleryThumbnailLoader?>? ThumbnailLoaderRequested;
    public event Func<string, SourceFile, Task<bool>>? ApplyImageRequested;
    public event Func<string, string, Task<ImportImageResult>>? ImportImageRequested;
    public event Action? ExitRequested;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = (SettingsViewModel)DataContext;
        _pages = [OverviewPage, GalleryPage, MonitorsPage, SystemPage, AboutPage];
        _navButtons = [NavOverview, NavGallery, NavMonitors, NavSystem, NavAbout];

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => RefreshStatuses();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };

        Loaded += OnLoaded;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { _statusTimer.Start(); RefreshStatuses(); }
            else
            {
                _statusTimer.Stop();
                _toastTimer.Stop();
                HideToast(immediate: true);
                _viewer?.Close();
            }
        };
        Closed += (_, _) =>
        {
            _galleryCts?.Cancel();
            _galleryCts?.Dispose();
            _galleryLoader?.Dispose();
            _viewer?.Close();
        };

        FitToWorkArea();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableRoundedCorners();
    }

    /// <summary>Windows 11 下让窗口外侧呈现系统圆角，边框风格与系统一致。</summary>
    private void TryEnableRoundedCorners()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var preference = DwmWindowCornerRound;
            DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var width = Math.Min(1200, Math.Max(940, work.Width - 80));
        var height = Math.Min(800, Math.Max(600, work.Height - 80));
        MinWidth = Math.Min(1010, width);
        MinHeight = Math.Min(660, height);
        Width = width;
        Height = height;
    }

    public void LoadConfig(AppConfig config, IReadOnlyList<MonitorStatus>? statuses = null, string? defaultCachePath = null)
    {
        // 不再重置 DataContext：那会让所有开关控件重新创建，保存时出现一次“闪一下”的动画。
        _viewModel.Load(config, statuses, defaultCachePath);
        // 保存后图片源可能变了，壁纸库需要重新读取。
        _galleryLoaded = false;
        ShowPage(Math.Max(_pageIndex, 0), animate: false);
    }

    public void RefreshStatuses()
    {
        var statuses = StatusRequested?.Invoke();
        if (statuses is not null) _viewModel.ApplyStatuses(statuses);
    }

    /// <summary>读取当前显示器图片源里的图片清单，并把第一页铺到壁纸库。</summary>
    private async Task LoadGalleryAsync(bool refresh)
    {
        var gallery = _viewModel.Gallery;
        var monitor = gallery.SelectedMonitor ?? _viewModel.Monitors.FirstOrDefault();
        if (monitor is null)
        {
            gallery.Reset("还没有检测到显示器。");
            _galleryLoaded = false;
            return;
        }
        if (!ReferenceEquals(gallery.SelectedMonitor, monitor)) gallery.SelectedMonitor = monitor;

        if (string.IsNullOrWhiteSpace(monitor.Model.Source.Address))
        {
            gallery.Reset("这台显示器还没有配置图片源，请先到「显示器与图片源」页面填写。");
            _galleryLoaded = false;
            return;
        }

        _galleryCts?.Cancel();
        _galleryCts?.Dispose();
        _galleryCts = new CancellationTokenSource();
        var ct = _galleryCts.Token;

        _galleryLoader?.Dispose();
        try
        {
            _galleryLoader = ThumbnailLoaderRequested?.Invoke(monitor.Model.MonitorId);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            _galleryLoader = null;
        }

        _galleryFiles.Clear();
        _galleryShown = 0;
        gallery.Reset("正在读取图片列表…");
        gallery.IsLoading = true;

        try
        {
            var files = ImagesRequested is null
                ? Array.Empty<SourceFile>()
                : await ImagesRequested(monitor.Model.MonitorId, refresh);
            if (ct.IsCancellationRequested) return;

            _galleryFiles.AddRange(files);
            _galleryLoaded = true;
            if (files.Count == 0)
            {
                gallery.Summary = "这个图片源里还没有图片。";
                return;
            }
            ShowMoreGallery();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Net.Http.HttpRequestException or NotSupportedException or
            ArgumentException or FormatException or InvalidOperationException)
        {
            _galleryLoaded = false;
            gallery.Summary = "读取失败：" + DescribeError(ex);
        }
        finally
        {
            gallery.IsLoading = false;
        }
    }

    private void ShowMoreGallery()
    {
        var gallery = _viewModel.Gallery;
        if (_galleryShown >= _galleryFiles.Count || _galleryShown >= GalleryMaxItems)
        {
            gallery.CanLoadMore = false;
            return;
        }

        var remaining = Math.Min(GalleryPageSize, GalleryMaxItems - _galleryShown);
        var batch = _galleryFiles.Skip(_galleryShown).Take(remaining).ToList();
        foreach (var file in batch)
        {
            var item = new GalleryImage(file);
            gallery.Images.Add(item);
            _ = LoadThumbnailAsync(item);
        }
        _galleryShown += batch.Count;

        gallery.CanLoadMore = _galleryShown < _galleryFiles.Count && _galleryShown < GalleryMaxItems;
        gallery.Summary = _galleryShown >= _galleryFiles.Count
            ? $"共 {_galleryFiles.Count:N0} 张"
            : _galleryShown >= GalleryMaxItems
                ? $"共 {_galleryFiles.Count:N0} 张 · 已显示前 {_galleryShown:N0} 张（浏览上限）"
                : $"共 {_galleryFiles.Count:N0} 张 · 已显示 {_galleryShown:N0} 张";
        gallery.NotifyCountChanged();
    }

    private async Task LoadThumbnailAsync(GalleryImage item)
    {
        var loader = _galleryLoader;
        var token = _galleryCts?.Token ?? CancellationToken.None;
        if (loader is null || token.IsCancellationRequested) return;

        try
        {
            await _thumbnailGate.WaitAsync(token);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            var thumbnail = await loader.LoadAsync(item.File, ThumbnailWidth, token);
            if (token.IsCancellationRequested) return;
            item.Thumbnail = thumbnail;
            item.Failed = thumbnail is null;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private async void GalleryMonitor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _viewModel.Gallery.SelectedMonitor is null) return;
        if (_pageIndex != GalleryIndex) { _galleryLoaded = false; return; }
        _galleryLoaded = false;
        await LoadGalleryAsync(refresh: false);
    }

    private async void GalleryRefresh_Click(object sender, RoutedEventArgs e)
    {
        ShowToast("正在重新读取图片列表…", false);
        await LoadGalleryAsync(refresh: true);
        RefreshStatuses();
    }

    private void GalleryMore_Click(object sender, RoutedEventArgs e) => ShowMoreGallery();

    private void GalleryImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GalleryImage item }) return;

        var index = _galleryFiles.FindIndex(f =>
            string.Equals(f.Location, item.File.Location, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = _viewModel.Gallery.Images.IndexOf(item);
        OpenViewer(Math.Max(index, 0));
    }

    /// <summary>打开大图查看窗口；单独申请一个加载器，避免壁纸库刷新时把查看中的图源关掉。</summary>
    private void OpenViewer(int index)
    {
        var monitor = _viewModel.Gallery.SelectedMonitor;
        if (monitor is null || _galleryFiles.Count == 0) return;

        GalleryThumbnailLoader? loader;
        try
        {
            loader = ThumbnailLoaderRequested?.Invoke(monitor.Model.MonitorId);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            loader = null;
        }

        if (loader is null)
        {
            ShowToast("无法打开图片查看器，请检查图片源配置。", true);
            return;
        }

        _viewer?.Close();

        var files = _galleryFiles.ToList();
        var viewer = new ImageViewerWindow(
            monitor.Name,
            files,
            index,
            (file, ct) => loader.LoadAsync(file, ViewerImageWidth, ct),
            async file =>
            {
                var applied = ApplyImageRequested is null ||
                    await ApplyImageRequested(monitor.Model.MonitorId, file);
                RefreshStatuses();
                return applied;
            },
            async file =>
            {
                var deleted = DeleteImageRequested is null ||
                    await DeleteImageRequested(monitor.Model.MonitorId, file.Location);
                if (!deleted) return false;

                _galleryFiles.RemoveAll(f =>
                    string.Equals(f.Location, file.Location, StringComparison.OrdinalIgnoreCase));
                RefreshStatuses();
                await LoadGalleryAsync(refresh: false);
                return true;
            })
        {
            Owner = this
        };

        viewer.Closed += (_, _) =>
        {
            loader.Dispose();
            if (ReferenceEquals(_viewer, viewer)) _viewer = null;
        };

        _viewer = viewer;
        viewer.Show();
    }

    private static string DescribeError(Exception ex)
    {
        var message = ex.GetBaseException().Message.Trim();
        if (string.IsNullOrWhiteSpace(message)) return "未知错误";
        return message.Length <= 80 ? message : message[..80] + "…";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var index = Math.Max(_pageIndex, 0);
        ShowPage(index, animate: false);
        AnimateIn(_pages[index], animate: true);
        AnimateHero();
    }

    private void AnimateHero()
    {
        if (HeroCard.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            HeroCard.RenderTransform = shift;
        }
        HeroCard.Opacity = 1;
        shift.Y = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HeroCard.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void ShowPage(int index, bool animate = true)
    {
        if (index < 0 || index >= _pages.Length) return;
        if (index == GalleryIndex && !_galleryLoaded) _ = LoadGalleryAsync(refresh: false);
        if (index == _pageIndex && _pages[index].Visibility == Visibility.Visible) return;
        _pageIndex = index;

        if (PageTitle is not null) PageTitle.Text = PageTitles[index];
        if (PageSubtitle is not null) PageSubtitle.Text = PageSubtitles[index];

        for (var i = 0; i < _pages.Length; i++)
        {
            var page = _pages[i];
            if (i == index)
            {
                page.Visibility = Visibility.Visible;
                AnimateIn(page, animate);
            }
            else
            {
                page.BeginAnimation(OpacityProperty, null);
                page.Visibility = Visibility.Collapsed;
            }
        }
        CancelPendingDelete();
        if (PageScroll is not null)
            PageScroll.VerticalScrollBarVisibility = index == 0
                ? ScrollBarVisibility.Hidden
                : ScrollBarVisibility.Auto;
        PageScroll?.ScrollToTop();

        var nav = _navButtons[index];
        if (nav.IsChecked != true) nav.IsChecked = true;
    }

    private static void AnimateIn(UIElement page, bool animate)
    {
        if (page.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            page.RenderTransform = shift;
        }
        page.BeginAnimation(OpacityProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        page.Opacity = 1;
        shift.Y = 0;
        if (!animate) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(330)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || sender is not RadioButton button) return;
        var index = button.CommandParameter is string text && int.TryParse(text, out var parsed) ? parsed : 0;
        ShowPage(index);
    }

    /// <summary>第一次点击进入确认状态并记下当时的图片，第二次点击才真正删除。</summary>
    private async void DeleteImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MonitorViewModel monitor } button) return;

        if (!monitor.DeletePending)
        {
            CancelPendingDelete();
            monitor.BeginDelete();
            _pendingDeleteButton = button;
            return;
        }

        var target = monitor.TakeDeleteTarget();
        CancelPendingDelete();
        if (string.IsNullOrEmpty(target) || DeleteImageRequested is null) return;

        var deleted = await DeleteImageRequested(monitor.Model.MonitorId, target);
        ShowToast(deleted ? "已从 NAS 删除这张图片。" : "删除失败：NAS 上的文件可能不存在或没有写入权限。", deleted);
        RefreshStatuses();
    }

    private void CancelPendingDelete()
    {
        _pendingDeleteButton = null;
        foreach (var monitor in _viewModel.Monitors) monitor.CancelDelete();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_pendingDeleteButton is not null &&
            (e.OriginalSource is not DependencyObject source || !IsInside(source, _pendingDeleteButton)))
            CancelPendingDelete();
        base.OnPreviewMouseDown(e);
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>手动检查更新：以“更新源”里填写的 GitHub 仓库为准。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateService.TryNormalizeRepository(_viewModel.UpdateRepository, out var repository))
        {
            _viewModel.SetUpdateError("更新源请填写 owner/repo，例如 acme/NAS-Wallpaper。");
            return;
        }

        _viewModel.BeginUpdateCheck();
        try
        {
            var info = CheckUpdateRequested is null ? null : await CheckUpdateRequested(repository);
            _viewModel.ApplyUpdateInfo(info);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
            or TaskCanceledException or IOException)
        {
            _viewModel.SetUpdateError("检查失败：" + ex.Message);
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PendingUpdate is not { } info || InstallUpdateRequested is null) return;

        var progress = new Progress<double>(value => _viewModel.SetUpdateProgress(value * 100));
        _viewModel.SetUpdateProgress(0);
        var installed = await InstallUpdateRequested(info, progress);
        if (!installed)
            _viewModel.SetUpdateError("更新失败：下载或替换文件时出错，可到 Release 页面手动下载。");
    }

    /// <summary>启动时后台检查到的更新，打开设置后直接显示。</summary>
    public void ShowPendingUpdate(UpdateInfo? info)
    {
        if (info is not null) _viewModel.ApplyUpdateInfo(info);
    }

    private void Path_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not MonitorViewModel monitor) return;
        if (monitor.Source.Kind != SourceKind.Local) return;
        e.Handled = true;
        PickFolder(box);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TextBox box }) PickFolder(box);
    }

    private void PickFolder(TextBox box)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择图片文件夹",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(box.Text) && Directory.Exists(box.Text))
            dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) != true) return;
        box.Text = dialog.FolderName;
        box.CaretIndex = box.Text.Length;
    }

    private void ChangeCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择缓存文件夹",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_viewModel.CachePathText)
                ? _viewModel.CachePathText
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var folder = Path.GetFullPath(dialog.FolderName);
            if (string.Equals(folder, Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("不能使用磁盘根目录作为缓存位置。", false);
                return;
            }
            var probe = Path.Combine(folder, ".nas-wallpaper-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            _viewModel.SetCacheDirectory(folder);
            ShowToast("缓存位置已更新，保存后生效。", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            ShowToast("该文件夹不可写入：" + ex.Message, false);
        }
    }

    private void ResetCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetCacheDirectory(null);
        ShowToast("已恢复默认缓存位置，保存后生效。", true);
    }

    private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = _viewModel.CachePathText;
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            ShowToast("无法打开缓存目录：" + ex.Message, false);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (!_viewModel.Validate(out var error))
        {
            _viewModel.MessageIsError = true;
            _viewModel.Message = error;
            ShowToast(error, error: true);
            return;
        }
        try
        {
            var config = _viewModel.Config.Clone();
            config.Monitors = _viewModel.Monitors.Select(m => m.Model).ToList();
            if (SaveRequested is not null) await SaveRequested(config);
            _viewModel.MessageIsError = false;
            _viewModel.Message = "设置已保存。图片会在后台校验后切换。";
            ShowToast("设置已保存", error: false);
            RefreshStatuses();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _viewModel.MessageIsError = true;
            _viewModel.Message = "无法保存：" + ex.Message;
            ShowToast("无法保存：" + ex.Message, error: true);
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (PreviousRequested is not null) await PreviousRequested();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (NextRequested is null) return;
        ShowToast("正在切换壁纸…", error: false);
        await NextRequested();
        RefreshStatuses();
        ShowToast("已切换到下一张", error: false);
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (PauseRequested is null) return;
        var paused = await PauseRequested();
        _viewModel.SetPaused(paused);
        ShowToast(paused ? "轮播已暂停" : "轮播已继续", error: false);
        RefreshStatuses();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatuses();
        ShowToast("状态已刷新", error: false);
    }

    /// <summary>只接受壁纸库页面上的图片拖入：图片会进入当前显示器的图片源并立即上桌面。</summary>
    private bool CanAcceptDrop(IDataObject data)
    {
        if (_pageIndex != GalleryIndex) return false;
        if (_viewModel.Gallery.SelectedMonitor is null) return false;
        return TryGetDroppedImages(data, out _);
    }

    private static bool TryGetDroppedImages(IDataObject data, out string[] files)
    {
        files = [];
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return false;
        files = paths.Where(p => IImageSource.IsImagePath(p)).ToArray();
        return files.Length > 0;
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        // 文本拖拽（例如把路径拖进输入框）交给控件自己处理。
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!CanAcceptDrop(e.Data) || !TryGetDroppedImages(e.Data, out var files)) return;
        var monitor = _viewModel.Gallery.SelectedMonitor!;
        e.Handled = true;

        var paths = files.Take(20).ToArray();
        ShowToast(paths.Length == 1 ? "正在导入图片…" : $"正在导入 {paths.Length} 张图片…", error: false);

        var imported = 0;
        var lastError = "";
        foreach (var path in paths)
        {
            ImportImageResult result;
            try
            {
                result = ImportImageRequested is null
                    ? new ImportImageResult(false, "导入功能尚未就绪。")
                    : await ImportImageRequested(monitor.Model.MonitorId, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Net.Http.HttpRequestException or NotSupportedException or ArgumentException)
            {
                result = new ImportImageResult(false, ex.Message);
            }

            if (result.Success) imported++;
            else lastError = result.Message;
        }

        RefreshStatuses();
        if (imported == 0)
        {
            ShowToast(string.IsNullOrWhiteSpace(lastError) ? "导入失败。" : lastError, error: true);
            return;
        }

        ShowToast(imported == 1
            ? "已上传到 NAS 并设为壁纸"
            : $"已上传 {imported} 张，最后一张已成为壁纸", error: false);
        await LoadGalleryAsync(refresh: true);
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: MonitorViewModel monitor } box)
            monitor.Source.EncryptedPassword = CredentialProtector.Protect(box.Password);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _closingToTray = false;
        ExitRequested?.Invoke();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SaveAsync();
        }
    }

    private void ShowToast(string text, bool error)
    {
        ToastText.Text = text;
        ToastGlyph.Text = error ? "\uE783" : "\uE73E";
        ToastGlyph.Foreground = (Brush)(error ? FindResource("Danger") : FindResource("Success"));

        Toast.BeginAnimation(OpacityProperty, null);
        ToastShift.BeginAnimation(TranslateTransform.YProperty, null);
        Toast.Opacity = 0;
        ToastShift.Y = 10;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast(bool immediate = false)
    {
        if (immediate)
        {
            Toast.BeginAnimation(OpacityProperty, null);
            Toast.Opacity = 0;
            return;
        }
        Toast.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.FromMilliseconds(260)));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closingToTray)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
