using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static BraviaTheatre.UI.Services.NativeMethods;

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
    private const int NIN_BALLOONUSERCLICK = WM_USER + 5;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETFOCUS = 0x00000003;
    private const int NIM_SETVERSION = 0x00000004;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    // Version 4 suppresses the shell tooltip unless NIF_SHOWTIP is requested.
    private const int NIF_SHOWTIP = 0x00000080;
    internal const int PresentationFlags =
        NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;

    // The tray icon itself, at full size, and no notification during quiet time.
    private const int NIIF_USER = 0x00000004;
    private const int NIIF_LARGE_ICON = 0x00000020;
    private const int NIIF_RESPECT_QUIET_TIME = 0x00000080;
    internal const int NotificationFlags = NIIF_USER | NIIF_LARGE_ICON | NIIF_RESPECT_QUIET_TIME;

    internal enum TrayCallbackAction
    {
        None,
        ToggleMouse,
        ToggleKeyboard,
        ContextMenu,
        NotificationClicked
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(
        ref NOTIFYICONIDENTIFIER identifier,
        out RECT iconLocation);

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _hwnd;
    private readonly uint _wmTaskbarCreated;
    private readonly uint _wmShowFlyout;
    private NOTIFYICONDATA _nid;
    private bool _isAdded;
    private bool _usesVersion4;
    private bool _hasPresentation;
    private bool _disposed;
    private DispatcherTimer? _explorerRecoveryTimer;
    private int _explorerRecoveryAttempts;
    private int _recoveryAttemptLimit;
    private bool _recoveringFromStartup;
    private int _lastAddError;

    // After an Explorer restart the new taskbar answers within moments.
    internal static readonly TimeSpan ExplorerRestartRetryInterval = TimeSpan.FromMilliseconds(200);
    internal const int ExplorerRestartAttempts = 8;

    // At sign-in Explorer can be too busy to answer for a while, and that is when Windows starts
    // the app from its Run entry; give it five minutes.
    internal static readonly TimeSpan StartupRetryInterval = TimeSpan.FromSeconds(2);
    internal const int StartupAttempts = 150;

    public Action? ContextMenuAction { get; set; }
    internal Action<TrayActivation>? ToggleAction { get; set; }
    public Action? ShowAction { get; set; }
    public Action? IconRecreatedAction { get; set; }
    public Action? NotificationClickedAction { get; set; }
    public Action<string>? LogAction { get; set; }

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
            uFlags = PresentationFlags,
            uCallbackMessage = WM_TRAYICON,
            szTip = "BRAVIA Theatre PC"
        };
    }

    /// <summary>Adds or updates the icon; false while Windows has not accepted it yet.</summary>
    public bool Show(Icon icon, string tooltip)
    {
        ThrowIfDisposed();
        SetPresentation(icon, tooltip);
        if (_isAdded) return Modify();
        if (Add()) return true;

        // Shell_NotifyIcon can time out while Explorer is busy. Only an Explorer restart used to
        // trigger another attempt, so an app started at sign-in could run for good without an icon.
        _recoveringFromStartup = true;
        BeginRecovery(StartupRetryInterval, StartupAttempts);
        return false;
    }

    public bool UpdateIcon(Icon icon, string tooltip)
    {
        if (_disposed) return false;
        SetPresentation(icon, tooltip);
        return _isAdded && Modify();
    }

    /// <summary>Shows a notification from the tray icon; Windows presents it as a toast.</summary>
    public bool ShowNotification(string title, string text)
    {
        if (_disposed || !_isAdded) return false;

        var notification = _nid;
        notification.uFlags = NIF_INFO;
        notification.szInfoTitle = title.Length > 63 ? title[..63] : title;
        notification.szInfo = text.Length > 255 ? text[..255] : text;
        notification.dwInfoFlags = NotificationFlags;
        if (Shell_NotifyIcon(NIM_MODIFY, ref notification)) return true;

        LogAction?.Invoke($"Tray notification failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return false;
    }

    internal bool TryGetIconBounds(out PixelRect bounds)
    {
        bounds = default;
        if (_disposed || !_isAdded) return false;

        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = _nid.uID
        };

        if (Shell_NotifyIconGetRect(ref identifier, out var rect) != 0) return false;
        bounds = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void SetPresentation(Icon icon, string tooltip)
    {
        _hasPresentation = true;
        _nid.uFlags = PresentationFlags;
        _nid.hIcon = icon.Handle;
        _nid.szTip = string.IsNullOrEmpty(tooltip)
            ? "BRAVIA Theatre PC"
            : tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private bool Add(bool logFailure = true)
    {
        _nid.uFlags = PresentationFlags;
        if (!Shell_NotifyIcon(NIM_ADD, ref _nid))
        {
            _isAdded = false;
            _lastAddError = Marshal.GetLastWin32Error();
            if (logFailure)
                LogAction?.Invoke($"Tray icon add failed (Win32 error {_lastAddError}).");
            return false;
        }

        _nid.uVersionOrTimeout = NOTIFYICON_VERSION_4;
        _usesVersion4 = Shell_NotifyIcon(NIM_SETVERSION, ref _nid);
        if (!_usesVersion4)
            LogAction?.Invoke($"Tray icon version negotiation failed (Win32 error {Marshal.GetLastWin32Error()}).");
        _isAdded = true;
        return true;
    }

    private bool Modify()
    {
        _nid.uFlags = PresentationFlags;
        if (Shell_NotifyIcon(NIM_MODIFY, ref _nid)) return true;
        LogAction?.Invoke($"Tray icon update failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var eventId = lParam.ToInt32() & 0xFFFF;
            var action = ClassifyCallback(eventId, _usesVersion4);
            if (action is TrayCallbackAction.ToggleMouse or TrayCallbackAction.ToggleKeyboard)
            {
                var activation = new TrayActivation(
                    action == TrayCallbackAction.ToggleKeyboard
                        ? TrayActivationKind.Keyboard
                        : TrayActivationKind.Mouse,
                    _usesVersion4 ? DecodeCallbackPoint(wParam) : null,
                    unchecked((uint)GetMessageTime()));
                ToggleAction?.Invoke(activation);
                handled = true;
            }
            else if (action == TrayCallbackAction.ContextMenu)
            {
                ContextMenuAction?.Invoke();
                handled = true;
            }
            else if (action == TrayCallbackAction.NotificationClicked)
            {
                NotificationClickedAction?.Invoke();
                handled = true;
            }
        }
        else if (msg == (int)_wmShowFlyout)
        {
            ShowAction?.Invoke();
            handled = true;
        }
        else if (msg == (int)_wmTaskbarCreated && _hasPresentation)
        {
            _isAdded = false;
            _usesVersion4 = false;
            _recoveringFromStartup = false;
            BeginRecovery(ExplorerRestartRetryInterval, ExplorerRestartAttempts);
            handled = true;
        }

        return IntPtr.Zero;
    }

    internal static TrayCallbackAction ClassifyCallback(int eventId, bool usesVersion4) =>
        usesVersion4
            ? eventId switch
            {
                NIN_SELECT => TrayCallbackAction.ToggleMouse,
                NIN_KEYSELECT => TrayCallbackAction.ToggleKeyboard,
                WM_CONTEXTMENU => TrayCallbackAction.ContextMenu,
                NIN_BALLOONUSERCLICK => TrayCallbackAction.NotificationClicked,
                _ => TrayCallbackAction.None
            }
            : eventId switch
            {
                WM_LBUTTONUP => TrayCallbackAction.ToggleMouse,
                WM_RBUTTONUP => TrayCallbackAction.ContextMenu,
                NIN_BALLOONUSERCLICK => TrayCallbackAction.NotificationClicked,
                _ => TrayCallbackAction.None
            };

    internal static PixelPoint DecodeCallbackPoint(IntPtr packedPoint)
    {
        var packed = unchecked((uint)packedPoint.ToInt64());
        return new PixelPoint(
            unchecked((short)(packed & 0xffff)),
            unchecked((short)((packed >> 16) & 0xffff)));
    }

    internal void RestoreTrayFocus()
    {
        if (_disposed || !_isAdded) return;
        var focusData = _nid;
        focusData.uFlags = 0;
        _ = Shell_NotifyIcon(NIM_SETFOCUS, ref focusData);
    }

    private void BeginRecovery(TimeSpan interval, int attemptLimit)
    {
        StopExplorerRecovery();
        _explorerRecoveryAttempts = 0;
        _recoveryAttemptLimit = attemptLimit;

        var timer = new DispatcherTimer(DispatcherPriority.Background, _hwndSource.Dispatcher)
        {
            Interval = interval
        };
        timer.Tick += (_, _) => TryCompleteExplorerRecovery();
        _explorerRecoveryTimer = timer;

        TryCompleteExplorerRecovery();
        if (ReferenceEquals(_explorerRecoveryTimer, timer))
            timer.Start();
    }

    private void TryCompleteExplorerRecovery()
    {
        if (_disposed)
        {
            StopExplorerRecovery();
            return;
        }

        _explorerRecoveryAttempts++;
        if (!_isAdded && !Add(logFailure: false))
        {
            if (_explorerRecoveryAttempts >= _recoveryAttemptLimit)
            {
                StopExplorerRecovery();
                LogAction?.Invoke($"Tray icon could not be added after {_explorerRecoveryAttempts} attempts (Win32 error {_lastAddError}).");
            }
            return;
        }

        if (TryGetIconBounds(out _) || _explorerRecoveryAttempts >= _recoveryAttemptLimit)
        {
            StopExplorerRecovery();
            if (_recoveringFromStartup)
            {
                _recoveringFromStartup = false;
                LogAction?.Invoke($"Tray icon added after {_explorerRecoveryAttempts + 1} attempts.");
            }
            IconRecreatedAction?.Invoke();
        }
    }

    private void StopExplorerRecovery()
    {
        _explorerRecoveryTimer?.Stop();
        _explorerRecoveryTimer = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeTrayIcon));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopExplorerRecovery();
        if (_isAdded)
        {
            _nid.uFlags = 0;
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isAdded = false;
        }
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
    }
}
