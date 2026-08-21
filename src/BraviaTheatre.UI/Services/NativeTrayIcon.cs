using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;

namespace BraviaTheatre.UI.Services;

public sealed class NativeTrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 100;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = WM_USER;
    private const int NIN_KEYSELECT = WM_USER + 1;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_GUID = 0x00000020;
    private const int FullFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID;
    private static readonly Guid TrayGuid = new("86AFAF46-103B-41C8-BBA9-7A0B802BFB0B");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
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
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _hwnd;
    private readonly uint _wmTaskbarCreated;
    private readonly uint _wmShowFlyout;
    private NOTIFYICONDATA _nid;
    private bool _isAdded;
    private bool _disposed;

    public ContextMenu? ContextMenu { get; set; }
    public Action? ShowAction { get; set; }
    public Action<string>? LogAction { get; set; }

    // Compatibility for callers; activation is now always interpreted as "show" rather than toggle.
    public Action? LeftClickAction { get => ShowAction; set => ShowAction = value; }

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
            uFlags = FullFlags,
            uCallbackMessage = WM_TRAYICON,
            szTip = "BRAVIA Theatre PC",
            guidItem = TrayGuid
        };
    }

    public bool Show(Icon icon, string tooltip)
    {
        ThrowIfDisposed();
        SetPresentation(icon, tooltip);
        return _isAdded ? Modify() : Add();
    }

    public bool UpdateIcon(Icon icon, string tooltip)
    {
        if (_disposed || !_isAdded) return false;
        SetPresentation(icon, tooltip);
        return Modify();
    }

    private void SetPresentation(Icon icon, string tooltip)
    {
        _nid.uFlags = FullFlags;
        _nid.hIcon = icon.Handle;
        _nid.szTip = string.IsNullOrEmpty(tooltip)
            ? "BRAVIA Theatre PC"
            : tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private bool Add()
    {
        _nid.uFlags = FullFlags;
        if (!Shell_NotifyIcon(NIM_ADD, ref _nid))
        {
            _isAdded = false;
            LogAction?.Invoke($"Tray icon add failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        _nid.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIcon(NIM_SETVERSION, ref _nid))
            LogAction?.Invoke($"Tray icon version negotiation failed (Win32 error {Marshal.GetLastWin32Error()}).");
        _isAdded = true;
        return true;
    }

    private bool Modify()
    {
        _nid.uFlags = FullFlags;
        if (Shell_NotifyIcon(NIM_MODIFY, ref _nid)) return true;
        LogAction?.Invoke($"Tray icon update failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var eventId = lParam.ToInt32() & 0xFFFF;
            if (eventId is WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT)
            {
                ShowAction?.Invoke();
                handled = true;
            }
            else if (eventId is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        else if (msg == (int)_wmShowFlyout)
        {
            ShowAction?.Invoke();
            handled = true;
        }
        else if (msg == (int)_wmTaskbarCreated && _isAdded)
        {
            _isAdded = false;
            Add();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (ContextMenu == null) return;
        SetForegroundWindow(_hwnd);
        ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        ContextMenu.HorizontalOffset = 0;
        ContextMenu.VerticalOffset = 0;
        ContextMenu.IsOpen = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeTrayIcon));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isAdded)
        {
            _nid.uFlags = NIF_GUID;
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isAdded = false;
        }
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
    }
}
