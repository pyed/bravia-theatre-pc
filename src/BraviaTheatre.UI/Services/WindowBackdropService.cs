using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace BraviaTheatre.UI.Services;

internal enum WindowBackdropKind
{
    MainWindow,
    TransientWindow
}

internal enum DwmSystemBackdropType
{
    Auto = 0,
    None = 1,
    MainWindow = 2,
    TransientWindow = 3,
    TabbedWindow = 4
}

internal readonly record struct WindowAppearancePlan(
    bool UseRoundedCorners,
    DwmSystemBackdropType BackdropType,
    bool UseOpaqueFallback);

internal static class WindowBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;

    private const int WmSysColorChange = 0x0015;
    private const int WmSettingChange = 0x001A;
    private const int WmThemeChanged = 0x031A;
    private const int WmDwmCompositionChanged = 0x031E;
    private const int WmDwmColorizationColorChanged = 0x0320;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    public static void Attach(Window window, WindowBackdropKind kind)
    {
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        void ApplyAppearance()
        {
            if (source == null || source.IsDisposed) return;

            var highContrast = SystemParameters.HighContrast;
            var plan = CreatePlan(Environment.OSVersion.Version, highContrast, kind);
            var handle = source.Handle;

            if (plan.UseRoundedCorners)
            {
                var corner = DwmwcpRound;
                _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            }

            var darkMode = !highContrast && AppsUseDarkMode() ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

            var backdropResult = -1;
            if (plan.BackdropType != DwmSystemBackdropType.Auto)
            {
                var backdrop = (int)plan.BackdropType;
                backdropResult = DwmSetWindowAttribute(
                    handle,
                    DwmwaSystemBackdropType,
                    ref backdrop,
                    sizeof(int));
            }

            var requestsBackdrop = backdropResult >= 0 &&
                                   plan.BackdropType is DwmSystemBackdropType.MainWindow or
                                       DwmSystemBackdropType.TransientWindow or
                                       DwmSystemBackdropType.TabbedWindow;
            var frame = requestsBackdrop
                ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }
                : new Margins();
            var frameResult = DwmExtendFrameIntoClientArea(handle, ref frame);

            var useOpaqueFallback = RequiresOpaqueFallback(plan, backdropResult, frameResult);
            window.SetResourceReference(
                Window.BackgroundProperty,
                useOpaqueFallback
                    ? "SolidBackgroundFillColorBaseBrush"
                    : "ControlFillColorTransparentBrush");

            source.CompositionTarget.BackgroundColor = useOpaqueFallback
                ? ResolveFallbackColor(window)
                : Colors.Transparent;
        }

        IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (IsAppearanceChangeMessage(message))
                window.Dispatcher.BeginInvoke(ApplyAppearance);
            return IntPtr.Zero;
        }

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source == null) return;
            hook = WindowProc;
            source.AddHook(hook);
            ApplyAppearance();
        }

        void OnClosed(object? sender, EventArgs args)
        {
            if (source != null && hook != null && !source.IsDisposed)
                source.RemoveHook(hook);
            window.SourceInitialized -= OnSourceInitialized;
            window.Closed -= OnClosed;
        }

        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
    }

    internal static WindowAppearancePlan CreatePlan(
        Version windowsVersion,
        bool highContrast,
        WindowBackdropKind kind)
    {
        var supportsRoundedCorners = windowsVersion >= new Version(10, 0, 22000);
        if (highContrast)
            return new WindowAppearancePlan(
                supportsRoundedCorners,
                DwmSystemBackdropType.None,
                UseOpaqueFallback: true);

        var backdrop = windowsVersion >= new Version(10, 0, 22621)
            ? kind == WindowBackdropKind.TransientWindow
                ? DwmSystemBackdropType.TransientWindow
                : DwmSystemBackdropType.TabbedWindow
            : DwmSystemBackdropType.Auto;

        return new WindowAppearancePlan(
            supportsRoundedCorners,
            backdrop,
            UseOpaqueFallback: backdrop == DwmSystemBackdropType.Auto);
    }

    internal static bool RequiresOpaqueFallback(WindowAppearancePlan plan, int backdropResult, int frameResult) =>
        plan.UseOpaqueFallback ||
        plan.BackdropType is DwmSystemBackdropType.Auto or DwmSystemBackdropType.None ||
        backdropResult < 0 ||
        frameResult < 0;

    internal static bool IsAppearanceChangeMessage(int message) =>
        message is WmSysColorChange or WmSettingChange or WmThemeChanged or
            WmDwmCompositionChanged or WmDwmColorizationColorChanged;

    private static Color ResolveFallbackColor(Window window)
    {
        if (SystemParameters.HighContrast)
            return SystemColors.WindowColor;

        return window.TryFindResource("SolidBackgroundFillColorBaseBrush") is SolidColorBrush brush
            ? brush.Color
            : SystemColors.WindowColor;
    }

    private static bool AppsUseDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int useLightTheme && useLightTheme == 0;
        }
        catch
        {
            return false;
        }
    }
}
