using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

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
    private const int NIM_SETFOCUS = 0x00000003;
    private const int NIM_SETVERSION = 0x00000004;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_GUID = 0x00000020;
    // Version 4 suppresses the shell tooltip unless NIF_SHOWTIP is requested.
    private const int NIF_SHOWTIP = 0x00000080;
    internal const int PresentationFlags =
        NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP;
    private static readonly Guid TrayGuid = new("86AFAF46-103B-41C8-BBA9-7A0B802BFB0B");

    internal enum TrayCallbackAction
    {
        None,
        ToggleMouse,
        ToggleKeyboard,
        ContextMenu
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _hwnd;
    private readonly uint _wmTaskbarCreated;
    private readonly uint _wmShowFlyout;
    private NOTIFYICONDATA _nid;
    private bool _isAdded;
    private bool _usesVersion4;
    private bool _hasPresentation;
    private bool _disposed;
    private readonly TrayContextMenuCoordinator _contextMenuCoordinator = new();
    private DispatcherTimer? _explorerRecoveryTimer;
    private int _explorerRecoveryAttempts;

    public ContextMenu? ContextMenu { get; set; }
    internal Action<TrayActivation>? ToggleAction { get; set; }
    public Action? ShowAction { get; set; }
    public Action? IconRecreatedAction { get; set; }
    public Action<string>? LogAction { get; set; }

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
            uFlags = PresentationFlags,
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
        if (_disposed) return false;
        SetPresentation(icon, tooltip);
        return _isAdded && Modify();
    }

    internal bool TryGetIconBounds(out PixelRect bounds)
    {
        bounds = default;
        if (_disposed || !_isAdded) return false;

        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = _nid.uID,
            guidItem = TrayGuid
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

    private bool Add()
    {
        _nid.uFlags = PresentationFlags;
        if (!Shell_NotifyIcon(NIM_ADD, ref _nid))
        {
            _isAdded = false;
            LogAction?.Invoke($"Tray icon add failed (Win32 error {Marshal.GetLastWin32Error()}).");
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
                HandleToggle(activation);
                handled = true;
            }
            else if (action == TrayCallbackAction.ContextMenu)
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
        else if (msg == (int)_wmTaskbarCreated && _hasPresentation)
        {
            _isAdded = false;
            _usesVersion4 = false;
            BeginExplorerRecovery();
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
                _ => TrayCallbackAction.None
            }
            : eventId switch
            {
                WM_LBUTTONUP => TrayCallbackAction.ToggleMouse,
                WM_RBUTTONUP => TrayCallbackAction.ContextMenu,
                _ => TrayCallbackAction.None
            };

    internal static PixelPoint DecodeCallbackPoint(IntPtr packedPoint)
    {
        var packed = unchecked((uint)packedPoint.ToInt64());
        return new PixelPoint(
            unchecked((short)(packed & 0xffff)),
            unchecked((short)((packed >> 16) & 0xffff)));
    }

    private void HandleToggle(TrayActivation activation)
    {
        if (_contextMenuCoordinator.TryDefer(activation))
        {
            if (ContextMenu?.IsOpen == true)
                ContextMenu.IsOpen = false;
            return;
        }

        InvokeToggle(activation);
    }

    private void InvokeToggle(TrayActivation activation)
    {
        if (ToggleAction != null)
            ToggleAction.Invoke(activation);
        else
            LeftClickAction?.Invoke();
    }

    private void ShowContextMenu()
    {
        var menu = ContextMenu;
        if (menu == null) return;
        if (menu.IsOpen || _contextMenuCoordinator.IsOpen) return;
        SetForegroundWindow(_hwnd);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;

        RoutedEventHandler? onClosed = null;
        onClosed = (_, _) =>
        {
            menu.Closed -= onClosed;
            var pendingActivation = _contextMenuCoordinator.Close();
            if (pendingActivation is { } activation)
            {
                _hwndSource.Dispatcher.BeginInvoke(
                    () => InvokeToggle(activation),
                    DispatcherPriority.Input);
            }
            else
            {
                RestoreTrayFocus();
            }
        };
        menu.Closed += onClosed;
        _contextMenuCoordinator.Open();
        menu.IsOpen = true;
    }

    private void RestoreTrayFocus()
    {
        if (_disposed || !_isAdded) return;
        var focusData = _nid;
        focusData.uFlags = NIF_GUID;
        _ = Shell_NotifyIcon(NIM_SETFOCUS, ref focusData);
    }

    private void BeginExplorerRecovery()
    {
        StopExplorerRecovery();
        _explorerRecoveryAttempts = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Background, _hwndSource.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
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
        if (!_isAdded && !Add())
        {
            if (_explorerRecoveryAttempts >= 8)
                StopExplorerRecovery();
            return;
        }

        if (TryGetIconBounds(out _) || _explorerRecoveryAttempts >= 8)
        {
            StopExplorerRecovery();
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
            _nid.uFlags = NIF_GUID;
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isAdded = false;
        }
        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
    }
}

internal sealed class TrayContextMenuCoordinator
{
    private TrayActivation? _pendingActivation;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        IsOpen = true;
        _pendingActivation = null;
    }

    public bool TryDefer(TrayActivation activation)
    {
        if (!IsOpen) return false;
        _pendingActivation = activation;
        return true;
    }

    public TrayActivation? Close()
    {
        IsOpen = false;
        var pending = _pendingActivation;
        _pendingActivation = null;
        return pending;
    }
}
