using NasWallpaper.Models;
using NasWallpaper.Services.Configuration;
using NasWallpaper.Services.Update;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace NasWallpaper.UI;

public partial class SettingsWindow : Window
{
    private static readonly string[] PageTitles = ["概览", "显示器与图片源", "缓存与系统", "关于"];
    private static readonly string[] PageSubtitles =
    [
        "所有显示器都在后台安静地工作。",
        "为每台显示器指定图片目录，以及它自己的节奏。",
        "缓存策略、桌面外观与开机启动。",
        "轻量、本地、只连接你自己的图片源。"
    ];

    private readonly SettingsViewModel _viewModel;
    private readonly StackPanel[] _pages;
    private readonly RadioButton[] _navButtons;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _toastTimer;
    private int _pageIndex = -1;
    private bool _closingToTray = true;
    private UIElement? _pendingDeleteButton;

    public event Func<AppConfig, Task>? SaveRequested;
    public event Func<Task>? NextRequested;
    public event Func<Task<bool>>? PauseRequested;
    public event Func<IReadOnlyList<MonitorStatus>>? StatusRequested;
    public event Func<string, string?, Task<bool>>? DeleteImageRequested;
    public event Func<string, Task<UpdateInfo?>>? CheckUpdateRequested;
    public event Func<UpdateInfo, IProgress<double>, Task<bool>>? InstallUpdateRequested;
    public event Action? ExitRequested;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = (SettingsViewModel)DataContext;
        _pages = [OverviewPage, MonitorsPage, SystemPage, AboutPage];
        _navButtons = [NavOverview, NavMonitors, NavSystem, NavAbout];

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => RefreshStatuses();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };

        Loaded += OnLoaded;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { _statusTimer.Start(); RefreshStatuses(); }
            else { _statusTimer.Stop(); _toastTimer.Stop(); HideToast(immediate: true); }
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
        ShowPage(Math.Max(_pageIndex, 0), animate: false);
    }

    public void RefreshStatuses()
    {
        var statuses = StatusRequested?.Invoke();
        if (statuses is not null) _viewModel.ApplyStatuses(statuses);
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
