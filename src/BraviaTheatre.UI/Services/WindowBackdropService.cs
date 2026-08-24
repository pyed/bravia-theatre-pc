using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    bool UseAcrylicComposition,
    bool UseOpaqueFallback);

internal static class WindowBackdropService
{
    private static readonly ConditionalWeakTable<Window, Action> RefreshActions = new();
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentDrawAllBorders = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int WmSysColorChange = 0x0015;
    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    public static void Attach(Window window, WindowBackdropKind kind)
    {
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        DispatcherOperation? pendingRefresh = null;
        var resetBackdropOnNextVisibleApply = true;

        void ApplyAppearance()
        {
            if (source == null || source.IsDisposed) return;

            var highContrast = SystemParameters.HighContrast;
            var plan = CreatePlan(Environment.OSVersion.Version, highContrast, kind);
            var handle = source.Handle;
            var planRequestsBackdrop = plan.UseAcrylicComposition ||
                plan.BackdropType is DwmSystemBackdropType.MainWindow or
                    DwmSystemBackdropType.TransientWindow or DwmSystemBackdropType.TabbedWindow;

            // WPF must expose a transparent composition surface before DWM applies
            // the material. Doing this after the DWM call can leave an opaque grey
            // surface until a later display/composition change refreshes the HWND.
            if (planRequestsBackdrop)
            {
                window.Background = Brushes.Transparent;
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            if (plan.UseRoundedCorners)
            {
                var corner = DwmwcpRound;
                _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            }

            var useDarkMode = !highContrast && AppsUseDarkMode();
            var darkMode = useDarkMode ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

            var backdropResult = -1;
            var accentResult = 0;
            if (plan.UseAcrylicComposition)
            {
                // DWMSBT_TRANSIENTWINDOW uses a much heavier tint than the shell
                // flyouts and appears as a flat grey panel on many systems. This
                // composition policy provides the live, blurred Windows acrylic
                // surface with an explicit light/dark tint.
                var noBackdrop = (int)DwmSystemBackdropType.None;
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaSystemBackdropType,
                    ref noBackdrop,
                    sizeof(int));
                accentResult = ApplyAccentPolicy(handle, enabled: true, useDarkMode);

                if (accentResult <= 0)
                {
                    // Retain a native material if the acrylic composition entry
                    // point is unavailable on an unexpected Windows build.
                    var fallbackBackdrop = (int)DwmSystemBackdropType.MainWindow;
                    backdropResult = DwmSetWindowAttribute(
                        handle,
                        DwmwaSystemBackdropType,
                        ref fallbackBackdrop,
                        sizeof(int));
                }
            }
            else
            {
                _ = ApplyAccentPolicy(handle, enabled: false, useDarkMode);

                if (plan.BackdropType != DwmSystemBackdropType.Auto)
                {
                    if (window.IsVisible && planRequestsBackdrop && resetBackdropOnNextVisibleApply)
                    {
                        // A backdrop selected while the HWND is hidden can remain an
                        // opaque composition surface. Change away from it once after
                        // the HWND becomes visible so DWM creates the live material.
                        var noBackdrop = (int)DwmSystemBackdropType.None;
                        _ = DwmSetWindowAttribute(
                            handle,
                            DwmwaSystemBackdropType,
                            ref noBackdrop,
                            sizeof(int));
                    }

                    var backdrop = (int)plan.BackdropType;
                    backdropResult = DwmSetWindowAttribute(
                        handle,
                        DwmwaSystemBackdropType,
                        ref backdrop,
                        sizeof(int));
                    if (window.IsVisible && planRequestsBackdrop && backdropResult >= 0)
                        resetBackdropOnNextVisibleApply = false;
                }
            }

            var requestsBackdrop = accentResult > 0 ||
                (backdropResult >= 0 &&
                 (plan.UseAcrylicComposition ||
                  plan.BackdropType is DwmSystemBackdropType.MainWindow or
                      DwmSystemBackdropType.TransientWindow or
                      DwmSystemBackdropType.TabbedWindow));
            var frame = requestsBackdrop
                ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }
                : new Margins();
            var frameResult = DwmExtendFrameIntoClientArea(handle, ref frame);

            var useOpaqueFallback = RequiresOpaqueFallback(
                plan,
                backdropResult,
                frameResult,
                accentResult);
            if (useOpaqueFallback)
            {
                window.SetResourceReference(
                    Window.BackgroundProperty,
                    "SolidBackgroundFillColorBaseBrush");
            }
            else
            {
                window.Background = Brushes.Transparent;
            }

            source.CompositionTarget.BackgroundColor = useOpaqueFallback
                ? ResolveFallbackColor(window)
                : Colors.Transparent;

            if (window.IsVisible)
            {
                _ = SetWindowPos(
                    handle,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
                _ = DwmFlush();
            }
        }

        void ScheduleAppearanceRefresh()
        {
            if (source == null || source.IsDisposed ||
                pendingRefresh is { Status: DispatcherOperationStatus.Pending })
            {
                return;
            }

            pendingRefresh = window.Dispatcher.BeginInvoke(() =>
            {
                pendingRefresh = null;
                ApplyAppearance();
            }, DispatcherPriority.Render);
        }

        IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (IsAppearanceChangeMessage(message))
            {
                resetBackdropOnNextVisibleApply = true;
                ScheduleAppearanceRefresh();
            }
            return IntPtr.Zero;
        }

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source == null) return;
            hook = WindowProc;
            source.AddHook(hook);
            RefreshActions.Remove(window);
            RefreshActions.Add(window, ApplyAppearance);
            ApplyAppearance();
        }

        void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (args.NewValue is true)
                ScheduleAppearanceRefresh();
            else
                resetBackdropOnNextVisibleApply = true;
        }

        void OnActivated(object? sender, EventArgs args) => ScheduleAppearanceRefresh();

        void OnContentRendered(object? sender, EventArgs args) => ScheduleAppearanceRefresh();

        void OnClosed(object? sender, EventArgs args)
        {
            if (source != null && hook != null && !source.IsDisposed)
                source.RemoveHook(hook);
            window.SourceInitialized -= OnSourceInitialized;
            window.IsVisibleChanged -= OnVisibilityChanged;
            window.Activated -= OnActivated;
            window.ContentRendered -= OnContentRendered;
            window.Closed -= OnClosed;
            RefreshActions.Remove(window);
        }

        window.SourceInitialized += OnSourceInitialized;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Activated += OnActivated;
        window.ContentRendered += OnContentRendered;
        window.Closed += OnClosed;
    }

    internal static void Refresh(Window window)
    {
        if (RefreshActions.TryGetValue(window, out var refresh))
            refresh();
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
                UseAcrylicComposition: false,
                UseOpaqueFallback: true);

        var useAcrylicComposition = supportsRoundedCorners && kind == WindowBackdropKind.TransientWindow;
        var backdrop = useAcrylicComposition
            ? DwmSystemBackdropType.None
            : windowsVersion >= new Version(10, 0, 22621)
                ? DwmSystemBackdropType.TabbedWindow
                : DwmSystemBackdropType.Auto;

        return new WindowAppearancePlan(
            supportsRoundedCorners,
            backdrop,
            useAcrylicComposition,
            UseOpaqueFallback: !useAcrylicComposition && backdrop == DwmSystemBackdropType.Auto);
    }

    internal static bool RequiresOpaqueFallback(
        WindowAppearancePlan plan,
        int backdropResult,
        int frameResult,
        int accentResult = 0) =>
        plan.UseOpaqueFallback ||
        frameResult < 0 ||
        (plan.UseAcrylicComposition
            ? accentResult <= 0 && backdropResult < 0
            : plan.BackdropType is DwmSystemBackdropType.Auto or DwmSystemBackdropType.None ||
              backdropResult < 0);

    internal static int AcrylicTintColor(bool useDarkMode) => useDarkMode
        ? unchecked((int)0x99202020)
        : unchecked((int)0xCCF7F7F7);

    internal static bool IsAppearanceChangeMessage(int message) =>
        message is WmSysColorChange or WmDisplayChange or WmSettingChange or WmDpiChanged or WmThemeChanged or
            WmDwmCompositionChanged or WmDwmColorizationColorChanged;

    private static Color ResolveFallbackColor(Window window)
    {
        if (SystemParameters.HighContrast)
            return SystemColors.WindowColor;

        return window.TryFindResource("SolidBackgroundFillColorBaseBrush") is SolidColorBrush brush
            ? brush.Color
            : SystemColors.WindowColor;
    }

    private static int ApplyAccentPolicy(IntPtr handle, bool enabled, bool useDarkMode)
    {
        var policy = new AccentPolicy
        {
            AccentState = enabled ? AccentEnableAcrylicBlurBehind : AccentDisabled,
            AccentFlags = enabled ? AccentDrawAllBorders : 0,
            GradientColor = enabled ? AcrylicTintColor(useDarkMode) : 0
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var dataPointer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, dataPointer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = dataPointer,
                SizeOfData = size
            };
            return SetWindowCompositionAttribute(handle, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(dataPointer);
        }
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
