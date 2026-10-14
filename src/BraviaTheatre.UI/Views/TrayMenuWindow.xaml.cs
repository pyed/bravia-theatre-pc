using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Services;
using static BraviaTheatre.UI.Services.NativeMethods;

namespace BraviaTheatre.UI.Views;

public partial class TrayMenuWindow : Window
{

    private readonly Action _openControls;
    private readonly Action _openSettings;
    private readonly Action _openAccount;
    private readonly Action _exit;
    private bool _isDismissing;

    public TrayMenuWindow(
        Action openControls,
        Action openSettings,
        Action openAccount,
        Action exit)
    {
        InitializeComponent();
        _openControls = openControls;
        _openSettings = openSettings;
        _openAccount = openAccount;
        _exit = exit;

        WindowBackdropService.Attach(this, WindowBackdropKind.TransientWindow);
        Deactivated += (_, _) => Dismiss();
    }

    public event EventHandler? Dismissed;

    public void UpdateState(SoundbarState state) =>
        TxtStatus.Text = SoundbarUiPresentationFactory.Create(state).TrayMenuHeader;

    internal void ShowMenu(PixelRect? requestedAnchor)
    {
        _isDismissing = false;
        Show();
        UpdateLayout();

        var handle = new WindowInteropHelper(this).Handle;
        var anchor = requestedAnchor ?? GetCursorAnchor();
        var monitorPoint = new PointNative { X = anchor.CenterX, Y = anchor.CenterY };
        var monitorHandle = MonitorFromPoint(monitorPoint, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (handle != IntPtr.Zero &&
            monitorHandle != IntPtr.Zero &&
            GetMonitorInfo(monitorHandle, ref monitorInfo) &&
            GetWindowRect(handle, out var windowRect))
        {
            var dpi = Math.Max(96u, GetDpiForWindow(handle));
            var margin = Math.Max(8, (int)Math.Round(8 * dpi / 96d));
            var placement = FlyoutPlacementCalculator.Calculate(
                ToPixelRect(monitorInfo.Monitor),
                ToPixelRect(monitorInfo.WorkArea),
                anchor,
                Math.Max(1, windowRect.Right - windowRect.Left),
                Math.Max(1, windowRect.Bottom - windowRect.Top),
                margin);

            _ = SetWindowPos(
                handle,
                IntPtr.Zero,
                placement.Bounds.Left,
                placement.Bounds.Top,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }

        Activate();
        Focus();
        WindowBackdropService.Refresh(this);
    }

    internal void Dismiss()
    {
        if (!IsVisible || _isDismissing) return;
        _isDismissing = true;
        Hide();
        try
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _isDismissing = false;
        }
    }

    internal void CloseForShutdown()
    {
        _isDismissing = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Dismiss();
    }

    private void OpenControls_Click(object sender, RoutedEventArgs e) => RunAfterDismiss(_openControls);

    private void Settings_Click(object sender, RoutedEventArgs e) => RunAfterDismiss(_openSettings);

    private void Account_Click(object sender, RoutedEventArgs e) => RunAfterDismiss(_openAccount);

    private void Exit_Click(object sender, RoutedEventArgs e) => RunAfterDismiss(_exit);

    private void RunAfterDismiss(Action action)
    {
        Dismiss();
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private static PixelRect GetCursorAnchor() =>
        GetCursorPos(out var cursor)
            ? PixelRect.FromPositionAndSize(cursor.X, cursor.Y, 1, 1)
            : PixelRect.FromPositionAndSize(0, 0, 1, 1);

    private static PixelRect ToPixelRect(RectNative rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
