using NasWallpaper.Models;
using System.Runtime.InteropServices;
using System.IO;

namespace NasWallpaper.Services.Wallpaper;

public interface IWallpaperService
{
    IReadOnlyList<DisplayInfo> GetDisplays();
    void SetWallpaper(string monitorId, string localPath, WallpaperStyle style);
}

public sealed class WindowsWallpaper : IWallpaperService, IDisposable
{
    private readonly IDesktopWallpaper _wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();

    /// <summary>
    /// 只返回当前真正连接的显示器：以系统枚举到的屏幕为准，
    /// 再按坐标把每块屏幕匹配到桌面壁纸接口的 monitor id，
    /// 避免显示器拔掉后残留的幽灵 id 被当成一块屏幕。
    /// </summary>
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var paths = new List<MonitorPath>();
        try
        {
            _wallpaper.GetMonitorDevicePathCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                _wallpaper.GetMonitorDevicePathAt(i, out var id);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var bounds = System.Drawing.Rectangle.Empty;
                try
                {
                    _wallpaper.GetMonitorRECT(id, out var rect);
                    bounds = new System.Drawing.Rectangle(rect.Left, rect.Top,
                        Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
                }
                catch (System.Runtime.InteropServices.COMException) { }
                paths.Add(new MonitorPath(id, bounds));
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }

        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0)
            return paths
                .Select((p, index) => new DisplayInfo(p.Id,
                    Describe(index == 0, index, p.Bounds.Width, p.Bounds.Height),
                    p.Bounds.Width, p.Bounds.Height, index == 0))
                .ToList();

        var used = new bool[paths.Count];
        var result = new List<DisplayInfo>(screens.Length);
        for (var screenIndex = 0; screenIndex < screens.Length; screenIndex++)
        {
            var screen = screens[screenIndex];
            var match = -1;
            for (var i = 0; i < paths.Count; i++)
            {
                if (used[i]) continue;
                if (!paths[i].Bounds.IsEmpty && paths[i].Bounds == screen.Bounds) { match = i; break; }
            }
            if (match < 0)
                for (var i = 0; i < paths.Count; i++)
                    if (!used[i]) { match = i; break; }
            if (match < 0) continue;

            used[match] = true;
            var bounds = paths[match].Bounds;
            var width = bounds.Width > 0 ? bounds.Width : screen.Bounds.Width;
            var height = bounds.Height > 0 ? bounds.Height : screen.Bounds.Height;
            result.Add(new DisplayInfo(paths[match].Id,
                Describe(screen.Primary, screenIndex, width, height), width, height, screen.Primary));
        }
        return result;
    }

    private static string Describe(bool primary, int index, int width, int height) =>
        $"{(primary ? "主显示器" : $"显示器 {index + 1}")} · {width} × {height}";

    private readonly record struct MonitorPath(string Id, System.Drawing.Rectangle Bounds);

    public void SetWallpaper(string monitorId, string localPath, WallpaperStyle style)
    {
        var path = Path.GetFullPath(localPath);
        if (!File.Exists(path)) throw new FileNotFoundException("壁纸文件不存在", path);
        _wallpaper.SetPosition(ToDesktopPosition(style));
        _wallpaper.SetWallpaper(monitorId, path);
    }

    /// <summary>
    /// DESKTOP_WALLPAPER_POSITION 的顺序是 居中0/平铺1/拉伸2/适应3/填充4/跨屏5，
    /// 与界面枚举顺序不同，必须显式映射，否则填充/适应/平铺会错位。
    /// </summary>
    private static int ToDesktopPosition(WallpaperStyle style) => style switch
    {
        WallpaperStyle.Center => 0,
        WallpaperStyle.Tile => 1,
        WallpaperStyle.Stretch => 2,
        WallpaperStyle.Fit => 3,
        WallpaperStyle.Fill => 4,
        WallpaperStyle.Span => 5,
        _ => 4
    };

    public void Dispose()
    {
        if (Marshal.IsComObject(_wallpaper)) Marshal.FinalReleaseComObject(_wallpaper);
    }
}

[ComImport, Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
internal class DesktopWallpaperClass { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
    void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);
    void GetMonitorDevicePathCount(out uint count);
    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out Rect rect);
    void SetBackgroundColor(uint color);
    void GetBackgroundColor(out uint color);
    void SetPosition(int position);
    void GetPosition(out WallpaperStyle position);
    void SetSlideshow(IntPtr items);
    void GetSlideshow(out IntPtr items);
    void SetSlideshowOptions(uint options, uint slideshowTick);
    void GetSlideshowOptions(out uint options, out uint slideshowTick);
    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, uint direction);
    void GetStatus(out uint state);
    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect { public int Left, Top, Right, Bottom; }
