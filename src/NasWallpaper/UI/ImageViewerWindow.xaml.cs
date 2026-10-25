using NasWallpaper.Models;
using NasWallpaper.Services.Preview;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;

namespace NasWallpaper.UI;

/// <summary>
/// 壁纸库的大图查看窗口：左右键浏览，Enter 设为壁纸，Delete 两次确认后删除 NAS 上的原图。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private readonly List<SourceFile> _files;
    private readonly Func<SourceFile, CancellationToken, Task<PreviewImage?>> _load;
    private readonly Func<SourceFile, Task<bool>> _apply;
    private readonly Func<SourceFile, Task<bool>> _delete;
    private readonly string _monitorName;
    private CancellationTokenSource? _cts;
    private int _index;
    private bool _applying;
    private bool _deleting;
    private bool _deletePending;
    private int _deleteConfirmToken;

    public ImageViewerWindow(
        string monitorName,
        IReadOnlyList<SourceFile> files,
        int index,
        Func<SourceFile, CancellationToken, Task<PreviewImage?>> load,
        Func<SourceFile, Task<bool>> apply,
        Func<SourceFile, Task<bool>> delete)
    {
        InitializeComponent();
        _monitorName = monitorName;
        _files = [.. files];
        _index = Math.Clamp(index, 0, Math.Max(0, _files.Count - 1));
        _load = load;
        _apply = apply;
        _delete = delete;

        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 40));
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height - 40));

        Loaded += async (_, _) => await ShowCurrentAsync();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    private async Task ShowCurrentAsync()
    {
        if (_files.Count == 0) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var file = _files[_index];

        TitleText.Text = Path.GetFileName(file.Location);
        CounterText.Text = $"{_index + 1} / {_files.Count}";
        MonitorNameText.Text = _monitorName;
        PreviewSizeText.Text = FormatSize(file.Length);
        ViewerImage.Source = null;
        StatusText.Text = "正在载入…";
        PreviousButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _files.Count - 1;
        SetDeletePending(false);

        try
        {
            var preview = await _load(file, ct);
            if (ct.IsCancellationRequested) return;
            if (preview is null)
            {
                StatusText.Text = "无法载入这张图片";
                return;
            }
            ViewerImage.Source = preview.Image;
            StatusText.Text = "";
            PreviewSizeText.Text =
                $"{FormatSize(file.Length)} · {preview.Width}×{preview.Height}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Net.Http.HttpRequestException or NotSupportedException or ArgumentException)
        {
            if (!ct.IsCancellationRequested) StatusText.Text = "载入失败：" + ex.Message;
        }
    }

    private void Step(int delta)
    {
        var next = Math.Clamp(_index + delta, 0, _files.Count - 1);
        if (next == _index) return;
        _index = next;
        _ = ShowCurrentAsync();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyCurrentAsync();

    private async Task ApplyCurrentAsync()
    {
        if (_applying || _files.Count == 0) return;
        _applying = true;
        ApplyButton.IsEnabled = false;
        try
        {
            var applied = await _apply(_files[_index]);
            StatusText.Text = applied ? "已设为壁纸" : "设置失败，请稍后再试";
            if (!applied) return;

            var shown = _index;
            await Task.Delay(1500);
            if (_index == shown && StatusText.Text == "已设为壁纸") StatusText.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Net.Http.HttpRequestException or ArgumentException)
        {
            StatusText.Text = "设置失败：" + ex.Message;
        }
        finally
        {
            _applying = false;
            ApplyButton.IsEnabled = true;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => _ = DeleteCurrentAsync();

    /// <summary>第一次点击进入确认状态，4 秒内再点一次才真正删除 NAS 上的原图。</summary>
    private async Task DeleteCurrentAsync()
    {
        if (_files.Count == 0 || _deleting) return;

        if (!_deletePending)
        {
            SetDeletePending(true);
            return;
        }

        SetDeletePending(false);
        _deleting = true;
        DeleteButton.IsEnabled = false;
        try
        {
            var deleted = await _delete(_files[_index]);
            if (!deleted)
            {
                StatusText.Text = "删除失败：文件可能已不存在，或没有写入权限";
                return;
            }

            _files.RemoveAt(_index);
            if (_files.Count == 0)
            {
                Close();
                return;
            }
            if (_index >= _files.Count) _index = _files.Count - 1;
            await ShowCurrentAsync();
            StatusText.Text = "已从 NAS 删除这张图片";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Net.Http.HttpRequestException or ArgumentException)
        {
            StatusText.Text = "删除失败：" + ex.Message;
        }
        finally
        {
            _deleting = false;
            DeleteButton.IsEnabled = true;
        }
    }

    private void SetDeletePending(bool pending)
    {
        _deletePending = pending;
        _deleteConfirmToken++;
        if (DeleteButton is null) return;

        DeleteButton.Content = pending ? "再点一次删除" : "删除这张";
        DeleteButton.Foreground = (Brush)FindResource(pending ? "Danger" : "TextTertiary");
        if (!pending) return;

        var token = _deleteConfirmToken;
        _ = ResetDeleteConfirmAsync(token);
    }

    private async Task ResetDeleteConfirmAsync(int token)
    {
        await Task.Delay(4000);
        if (_deleteConfirmToken == token) SetDeletePending(false);
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_deletePending) SetDeletePending(false);
                else Close();
                e.Handled = true;
                break;
            case Key.Left:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Right:
                Step(1);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = ApplyCurrentAsync();
                e.Handled = true;
                break;
            case Key.Delete:
                _ = DeleteCurrentAsync();
                e.Handled = true;
                break;
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024
        ? $"{bytes / 1024d / 1024d:0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}
