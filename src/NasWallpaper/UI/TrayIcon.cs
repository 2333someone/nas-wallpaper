using System.Drawing;
using System.Runtime.InteropServices;

namespace NasWallpaper.UI;

public sealed partial class TrayIcon : IDisposable
{
    private readonly NativeWindow _window;
    private readonly uint _callback = RegisterWindowMessage("NAS Wallpaper Tray");
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Icon? _icon = LoadAppIcon();
    private NotifyIconData _data;
    private bool _added;

    public event Action? OpenRequested;
    public event Func<Task>? NextRequested;
    public event Func<Task>? PauseRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _window = new NativeWindow(this);
        _window.CreateHandle(new CreateParams());
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), WindowHandle = _window.Handle,
            Id = 1, Flags = 0x1 | 0x2 | 0x4, CallbackMessage = _callback,
            Icon = _icon?.Handle ?? SystemIcons.Application.Handle,
            Tip = "NAS Wallpaper"
        };
        Add();
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            return Environment.ProcessPath is { } path ? Icon.ExtractAssociatedIcon(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void Add()
    {
        ShellNotifyIcon(0, ref _data);
        _added = true;
    }

    public void UpdatePauseText(bool paused)
    {
        _data.Tip = paused ? "NAS Wallpaper · 已暂停" : "NAS Wallpaper";
        if (_added) ShellNotifyIcon(1, ref _data);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 1001, "打开设置");
        AppendMenu(menu, 0, 1002, "下一张");
        AppendMenu(menu, 0, 1003, "暂停 / 继续轮播");
        AppendMenu(menu, 0x800, 0, null);
        AppendMenu(menu, 0, 1004, "退出");
        GetCursorPos(out var point);
        SetForegroundWindow(_window.Handle);
        var command = TrackPopupMenu(menu, 0x100, point.X, point.Y, 0, _window.Handle, IntPtr.Zero);
        DestroyMenu(menu);
        switch (command)
        {
            case 1001: OpenRequested?.Invoke(); break;
            case 1002: if (NextRequested is not null) _ = NextRequested(); break;
            case 1003: if (PauseRequested is not null) _ = PauseRequested(); break;
            case 1004: ExitRequested?.Invoke(); break;
        }
    }

    private void OnMessage(ref Message message)
    {
        if (message.Msg == _taskbarCreated) { _added = false; Add(); }
        if (message.Msg != _callback) return;
        var action = unchecked((int)message.LParam.ToInt64()) & 0xffff;
        if (action == 0x0202 || action == 0x0205) ShowMenu();
        else if (action == 0x0203) OpenRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_added) ShellNotifyIcon(2, ref _data);
        _window.DestroyHandle();
        _icon?.Dispose();
    }

    private sealed class NativeWindow(TrayIcon owner) : System.Windows.Forms.NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            owner.OnMessage(ref m);
            base.WndProc(ref m);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr WindowHandle; public uint Id; public uint Flags;
        public uint CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags; public Guid BalloonIcon; public IntPtr BalloonIconHandle;
    }
}
