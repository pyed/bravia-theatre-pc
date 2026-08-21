using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace BraviaTheatre.UI.Services;

public sealed class NativeTrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 100;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _hwnd;
    private readonly uint _wmTaskbarCreated;
    private readonly uint _wmShowFlyout;
    private NOTIFYICONDATA _nid;
    private bool _isAdded;

    public ContextMenu? ContextMenu { get; set; }
    public Action? LeftClickAction { get; set; }

    public NativeTrayIcon()
    {
        var parameters = new HwndSourceParameters("BraviaTheatre_TrayMsgWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        _hwnd = _hwndSource.Handle;

        _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _wmShowFlyout = RegisterWindowMessage("BRAVIA_THEATRE_PC_SHOW_FLYOUT");

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            szTip = "BRAVIA Theatre PC"
        };
    }

    public void Show(Icon icon, string tooltip)
    {
        _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        _nid.hIcon = icon.Handle;
        _nid.szTip = string.IsNullOrEmpty(tooltip) ? "BRAVIA Theatre PC" : (tooltip.Length > 127 ? tooltip[..127] : tooltip);

        if (!_isAdded)
        {
            Shell_NotifyIcon(NIM_ADD, ref _nid);
            _nid.uVersionOrTimeout = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref _nid);
            _isAdded = true;
        }
        else
        {
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }
    }

    public void UpdateIcon(Icon icon, string tooltip)
    {
        if (!_isAdded) return;
        _nid.uFlags = NIF_ICON | NIF_TIP;
        _nid.hIcon = icon.Handle;
        _nid.szTip = string.IsNullOrEmpty(tooltip) ? "BRAVIA Theatre PC" : (tooltip.Length > 127 ? tooltip[..127] : tooltip);
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int eventId = lParam.ToInt32() & 0xFFFF;
            if (eventId == WM_LBUTTONUP || eventId == WM_LBUTTONDBLCLK)
            {
                LeftClickAction?.Invoke();
                handled = true;
            }
            else if (eventId == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        else if (msg == (int)_wmShowFlyout)
        {
            LeftClickAction?.Invoke();
            handled = true;
        }
        else if (msg == (int)_wmTaskbarCreated)
        {
            if (_isAdded)
            {
                Shell_NotifyIcon(NIM_ADD, ref _nid);
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (ContextMenu == null) return;
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        ContextMenu.HorizontalOffset = pt.X;
        ContextMenu.VerticalOffset = pt.Y;
        ContextMenu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_isAdded)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isAdded = false;
        }
        _hwndSource.Dispose();
    }
}
