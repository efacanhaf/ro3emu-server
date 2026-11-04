using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Ro3Emu.Launcher;

/// <summary>
/// The launcher's tray icon while the game runs, straight on Shell_NotifyIcon: WinForms' NotifyIcon would add ~11 MB to
/// the single-file exe. Left click shows the launcher; right click opens Show / Exit; a balloon can say why it is there.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    const int WM_APP_TRAY = 0x8000 + 1;
    const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203;
    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const uint MF_STRING = 0, TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    const int CmdShow = 1, CmdExit = 2;

    readonly HwndSource _window;
    readonly IntPtr _icon;
    readonly Action _show;
    readonly Action _exit;
    readonly uint _taskbarCreated;
    readonly string _tip;
    bool _added;

    public TrayIcon(string exePath, string tip, Action show, Action exit)
    {
        _show = show;
        _exit = exit;
        // a hidden top-level window (never shown) receives the icon's mouse messages and Explorer's TaskbarCreated broadcast
        _window = new HwndSource(new HwndSourceParameters("Ro3EmuLauncherTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _icon = ExtractIcon(IntPtr.Zero, exePath, 0);
        if (_icon == IntPtr.Zero || _icon == new IntPtr(1)) _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _tip = tip;
    }

    public bool Visible
    {
        get => _added;
        set
        {
            if (value == _added) return;
            var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _added = value && Shell_NotifyIcon(NIM_ADD, ref data);
            if (!value) Shell_NotifyIcon(NIM_DELETE, ref data);
        }
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.uTimeoutOrVersion = 4000;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            int mouse = lParam.ToInt32() & 0xFFFF;
            if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK) _show();
            else if (mouse == WM_RBUTTONUP) ShowMenu();
            handled = true;
        }
        else if (msg == _taskbarCreated && _added)
        {
            // Explorer restarted: the icon is gone, add it again
            _added = false;
            Visible = true;
        }
        return IntPtr.Zero;
    }

    void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, CmdShow, "Show launcher");
        AppendMenu(menu, MF_STRING, CmdExit, "Exit");
        GetCursorPos(out var point);
        SetForegroundWindow(_window.Handle); // so the menu closes when the player clicks elsewhere
        int chosen = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.X, point.Y, 0, _window.Handle, IntPtr.Zero);
        DestroyMenu(menu);
        if (chosen == CmdShow) _show();
        else if (chosen == CmdExit) _exit();
    }

    public void Dispose()
    {
        Visible = false;
        _window.Dispose();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, int id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
