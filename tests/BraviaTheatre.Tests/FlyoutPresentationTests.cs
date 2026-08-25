using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

public class FlyoutPresentationTests
{
    [Fact]
    public void Version4TrayIconRequestsTheStandardTooltip()
    {
        Assert.NotEqual(0, NativeTrayIcon.PresentationFlags & 0x00000080); // NIF_SHOWTIP
    }

    [Fact]
    public void PortableTrayIdentityIsScopedToExecutablePathAndUid()
    {
        Assert.Equal(0, NativeTrayIcon.PresentationFlags & 0x00000020); // NIF_GUID
    }

    [Theory]
    [InlineData(0x0400, 1)] // NIN_SELECT -> ToggleMouse
    [InlineData(0x0401, 2)] // NIN_KEYSELECT -> ToggleKeyboard
    [InlineData(0x007B, 3)] // WM_CONTEXTMENU -> ContextMenu
    public void Version4CallbacksUseModernShellNotifications(
        int callback,
        int expected)
    {
        Assert.Equal(
            (NativeTrayIcon.TrayCallbackAction)expected,
            NativeTrayIcon.ClassifyCallback(callback, usesVersion4: true));
    }

    [Theory]
    [InlineData(0x0202, 1)] // WM_LBUTTONUP -> ToggleMouse
    [InlineData(0x0205, 3)] // WM_RBUTTONUP -> ContextMenu
    public void LegacyCallbacksUseMouseMessages(
        int callback,
        int expected)
    {
        Assert.Equal(
            (NativeTrayIcon.TrayCallbackAction)expected,
            NativeTrayIcon.ClassifyCallback(callback, usesVersion4: false));
    }

    [Theory]
    [InlineData(0x0202)] // WM_LBUTTONUP
    [InlineData(0x0205)] // WM_RBUTTONUP
    public void Version4ModeIgnoresLegacyCallbacks(int callback)
    {
        Assert.Equal(
            NativeTrayIcon.TrayCallbackAction.None,
            NativeTrayIcon.ClassifyCallback(callback, usesVersion4: true));
    }

    [Theory]
    [InlineData(0x0400)] // NIN_SELECT
    [InlineData(0x0401)] // NIN_KEYSELECT
    [InlineData(0x007B)] // WM_CONTEXTMENU
    public void LegacyModeIgnoresVersion4Callbacks(int callback)
    {
        Assert.Equal(
            NativeTrayIcon.TrayCallbackAction.None,
            NativeTrayIcon.ClassifyCallback(callback, usesVersion4: false));
    }

    [Fact]
    public void Version4CallbackPointPreservesNegativeMonitorCoordinates()
    {
        const short x = -18;
        const short y = -320;
        var packed = (uint)unchecked((ushort)x) | ((uint)unchecked((ushort)y) << 16);

        var point = NativeTrayIcon.DecodeCallbackPoint(new IntPtr(unchecked((int)packed)));

        Assert.Equal(new PixelPoint(x, y), point);
    }

    [Fact]
    public void DeactivateThenSameTrayClickClosesWithoutReopening()
    {
        var controller = new FlyoutTransitionController();
        CompleteOpen(controller);

        var close = controller.Hide(causedByDeactivation: true);
        Assert.Equal(FlyoutPresentationState.Closing, controller.State);

        var callback = controller.ToggleFromTray(sameDeactivationInteraction: true);

        Assert.False(callback.Exists);
        Assert.True(controller.Complete(close));
        Assert.Equal(FlyoutPresentationState.Hidden, controller.State);
    }

    [Fact]
    public void DelayedCallbackAfterCloseStillDoesNotReopenSameTrayInteraction()
    {
        var controller = new FlyoutTransitionController();
        CompleteOpen(controller);

        var close = controller.Hide(causedByDeactivation: true);
        Assert.True(controller.Complete(close));
        Assert.Equal(FlyoutPresentationState.Hidden, controller.State);

        var sameInteraction = TrayInteractionCorrelator.IsSameInteraction(
            new TrayActivation(TrayActivationKind.Mouse, new PixelPoint(1810, 1058), 1_250),
            deactivationMessageTime: 1_000,
            deactivationPoint: new PixelPoint(1810, 1058),
            trayBounds: new PixelRect(1790, 1040, 1830, 1080),
            controller.IsAwaitingCorrelatedTrayToggle);
        var callback = controller.ToggleFromTray(sameInteraction);

        Assert.False(callback.Exists);
        Assert.Equal(FlyoutPresentationState.Hidden, controller.State);
    }

    [Fact]
    public void TrayContextMenuDoesNotConsumeTheNextLeftClick()
    {
        var controller = new FlyoutTransitionController();
        CompleteOpen(controller);
        var close = controller.Hide(causedByDeactivation: true);

        controller.ResolveTrayInteraction();
        Assert.True(controller.Complete(close));

        var reopen = controller.ToggleFromTray(sameDeactivationInteraction: true);

        Assert.True(reopen.Exists);
        Assert.Equal(FlyoutPresentationState.Opening, controller.State);
    }

    [Fact]
    public void LeftClickWaitsForAnOpenContextMenuToClose()
    {
        var coordinator = new TrayContextMenuCoordinator();
        var activation = new TrayActivation(
            TrayActivationKind.Mouse,
            new PixelPoint(1810, 1058),
            1_250);

        coordinator.Open();

        Assert.True(coordinator.TryDefer(activation));
        Assert.Equal(activation, coordinator.Close());
        Assert.False(coordinator.IsOpen);
        Assert.Null(coordinator.Close());
        Assert.False(coordinator.TryDefer(activation));
    }

    [Fact]
    public void AuthenticationFailureIsPresentedAsSignInRequiredInsteadOfOffline()
    {
        var presentation = SoundbarUiPresentationFactory.Create(
            SoundbarState.Disconnected with { AuthRequired = true });

        Assert.True(presentation.ShowAuthenticationPrompt);
        Assert.False(presentation.ControlsEnabled);
        Assert.Contains("sign-in required", presentation.TrayTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sign-in required", presentation.TrayMenuHeader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retrying", presentation.TrayTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaterDistinctTrayClickReopensWhileClosing()
    {
        var controller = new FlyoutTransitionController();
        CompleteOpen(controller);
        var staleClose = controller.Hide(causedByDeactivation: true);

        var reopen = controller.ToggleFromTray(sameDeactivationInteraction: false);

        Assert.Equal(FlyoutPresentationState.Opening, controller.State);
        Assert.False(controller.Complete(staleClose));
        Assert.True(controller.Complete(reopen));
        Assert.Equal(FlyoutPresentationState.Open, controller.State);
    }

    [Fact]
    public void ShowOnlyRequestReversesCloseAndStaleCompletionCannotHideFlyout()
    {
        var controller = new FlyoutTransitionController();
        CompleteOpen(controller);
        var staleClose = controller.Hide(causedByDeactivation: false);

        var show = controller.Show();

        Assert.Equal(FlyoutPresentationState.Opening, controller.State);
        Assert.False(controller.Complete(staleClose));
        Assert.True(controller.Complete(show));
        Assert.Equal(FlyoutPresentationState.Open, controller.State);
        Assert.False(controller.Show().Exists);
    }

    [Fact]
    public void LongHeldTrayClickIsStillOneInteraction()
    {
        var bounds = new PixelRect(1790, 1040, 1830, 1080);
        var same = TrayInteractionCorrelator.IsSameInteraction(
            new TrayActivation(TrayActivationKind.Mouse, new PixelPoint(1812, 1059), 26_000),
            deactivationMessageTime: 1_000,
            deactivationPoint: new PixelPoint(1810, 1058),
            trayBounds: bounds,
            deactivationClosePending: true);

        Assert.True(same);
    }

    [Fact]
    public void KeyboardTrayActivationCorrelatesWithoutAStableCursorPoint()
    {
        var same = TrayInteractionCorrelator.IsSameInteraction(
            new TrayActivation(TrayActivationKind.Keyboard, ScreenPoint: null, MessageTime: 2_500),
            deactivationMessageTime: 2_000,
            deactivationPoint: null,
            trayBounds: null,
            deactivationClosePending: true);

        Assert.True(same);
    }

    [Fact]
    public void LaterKeyboardActivationIsASeparateRequestToOpen()
    {
        var same = TrayInteractionCorrelator.IsSameInteraction(
            new TrayActivation(TrayActivationKind.Keyboard, ScreenPoint: null, MessageTime: 4_001),
            deactivationMessageTime: 2_000,
            deactivationPoint: null,
            trayBounds: null,
            deactivationClosePending: true);

        Assert.False(same);
    }

    [Fact]
    public void CorrelationHandlesGetMessageTimeWraparound()
    {
        var same = TrayInteractionCorrelator.IsSameInteraction(
            new TrayActivation(TrayActivationKind.Keyboard, ScreenPoint: null, MessageTime: 20),
            deactivationMessageTime: uint.MaxValue - 20,
            deactivationPoint: null,
            trayBounds: null,
            deactivationClosePending: true);

        Assert.True(same);
        Assert.Equal(TimeSpan.FromMilliseconds(41), TrayInteractionCorrelator.Elapsed(uint.MaxValue - 20, 20));
    }

    [Fact]
    public void UnrelatedOrExpiredActivationIsNotCorrelated()
    {
        var bounds = new PixelRect(1790, 1040, 1830, 1080);

        Assert.False(TrayInteractionCorrelator.IsSameInteraction(
            new TrayActivation(TrayActivationKind.Mouse, new PixelPoint(1810, 1058), 31_001),
            deactivationMessageTime: 1_000,
            deactivationPoint: new PixelPoint(1810, 1058),
            trayBounds: bounds,
            deactivationClosePending: true));
        Assert.False(TrayInteractionCorrelator.IsSameInteraction(
            new TrayActivation(TrayActivationKind.Mouse, new PixelPoint(1810, 1058), 1_100),
            deactivationMessageTime: 1_000,
            deactivationPoint: new PixelPoint(500, 500),
            trayBounds: bounds,
            deactivationClosePending: true));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040, 3)]
    [InlineData(0, 40, 1920, 1080, 1)]
    [InlineData(48, 0, 1920, 1080, 0)]
    [InlineData(0, 0, 1872, 1080, 2)]
    public void PlacementDetectsEveryTaskbarEdge(
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int expectedEdgeValue)
    {
        var expected = (TaskbarEdge)expectedEdgeValue;
        var monitor = new PixelRect(0, 0, 1920, 1080);
        var work = new PixelRect(workLeft, workTop, workRight, workBottom);
        var anchor = expected switch
        {
            TaskbarEdge.Left => new PixelRect(4, 500, 28, 524),
            TaskbarEdge.Top => new PixelRect(1700, 4, 1724, 28),
            TaskbarEdge.Right => new PixelRect(1892, 500, 1916, 524),
            _ => new PixelRect(1700, 1052, 1724, 1076)
        };

        var placement = FlyoutPlacementCalculator.Calculate(monitor, work, anchor, 384, 480, 12);

        Assert.Equal(expected, placement.TaskbarEdge);
        Assert.True(placement.Bounds.Left >= work.Left);
        Assert.True(placement.Bounds.Top >= work.Top);
        Assert.True(placement.Bounds.Right <= work.Right);
        Assert.True(placement.Bounds.Bottom <= work.Bottom);
    }

    [Fact]
    public void PlacementSupportsNegativeCoordinateMonitorsAndAutoHiddenTaskbar()
    {
        var monitor = new PixelRect(-2560, -120, 0, 1320);
        var anchor = new PixelRect(-40, 600, -16, 624);

        var placement = FlyoutPlacementCalculator.Calculate(
            monitor,
            monitor,
            anchor,
            flyoutWidth: 576,
            flyoutHeight: 720,
            margin: 18);

        Assert.Equal(TaskbarEdge.Right, placement.TaskbarEdge);
        Assert.Equal(-594, placement.Bounds.Left);
        Assert.True(placement.Bounds.Top >= monitor.Top);
        Assert.True(placement.Bounds.Bottom <= monitor.Bottom);
    }

    [Fact]
    public void PlacementConstrainsAnOversizedFlyoutToTheWorkArea()
    {
        var work = new PixelRect(100, 50, 400, 250);

        var placement = FlyoutPlacementCalculator.Calculate(
            work,
            work,
            new PixelRect(350, 220, 380, 245),
            flyoutWidth: 600,
            flyoutHeight: 400,
            margin: 12);

        Assert.Equal(276, placement.Bounds.Width);
        Assert.Equal(176, placement.Bounds.Height);
        Assert.True(placement.Bounds.Left >= work.Left);
        Assert.True(placement.Bounds.Top >= work.Top);
        Assert.True(placement.Bounds.Right <= work.Right);
        Assert.True(placement.Bounds.Bottom <= work.Bottom);
    }

    [Fact]
    public void Windows11UsesTintedAcrylicCompositionForTransientFlyouts()
    {
        var plan = WindowBackdropService.CreatePlan(
            new Version(10, 0, 22621),
            highContrast: false,
            WindowBackdropKind.TransientWindow);

        Assert.True(plan.UseRoundedCorners);
        Assert.Equal(DwmSystemBackdropType.None, plan.BackdropType);
        Assert.True(plan.UseAcrylicComposition);
        Assert.False(plan.UseOpaqueFallback);
    }

    [Fact]
    public void Windows11Build22621UsesMicaAltForMainWindows()
    {
        var plan = WindowBackdropService.CreatePlan(
            new Version(10, 0, 22621),
            highContrast: false,
            WindowBackdropKind.MainWindow);

        Assert.True(plan.UseRoundedCorners);
        Assert.Equal(DwmSystemBackdropType.TabbedWindow, plan.BackdropType);
        Assert.False(plan.UseAcrylicComposition);
        Assert.False(plan.UseOpaqueFallback);
    }

    [Theory]
    [InlineData(19045, false)]
    [InlineData(22000, true)]
    public void UnsupportedBackdropBuildsUseOpaqueThemedFallback(int build, bool rounded)
    {
        var plan = WindowBackdropService.CreatePlan(
            new Version(10, 0, build),
            highContrast: false,
            WindowBackdropKind.MainWindow);

        Assert.Equal(rounded, plan.UseRoundedCorners);
        Assert.Equal(DwmSystemBackdropType.Auto, plan.BackdropType);
        Assert.False(plan.UseAcrylicComposition);
        Assert.True(plan.UseOpaqueFallback);
    }

    [Fact]
    public void HighContrastDisablesDecorativeBackdrop()
    {
        var plan = WindowBackdropService.CreatePlan(
            new Version(10, 0, 26100),
            highContrast: true,
            WindowBackdropKind.TransientWindow);

        Assert.True(plan.UseRoundedCorners);
        Assert.Equal(DwmSystemBackdropType.None, plan.BackdropType);
        Assert.False(plan.UseAcrylicComposition);
        Assert.True(plan.UseOpaqueFallback);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(-1, 0)]
    public void FailedAcrylicAndFallbackCallsForceAnOpaqueFallback(int accentResult, int frameResult)
    {
        var plan = WindowBackdropService.CreatePlan(
            new Version(10, 0, 26100),
            highContrast: false,
            WindowBackdropKind.TransientWindow);

        Assert.True(WindowBackdropService.RequiresOpaqueFallback(
            plan,
            backdropResult: -1,
            frameResult,
            accentResult));
    }

    [Fact]
    public void SuccessfulAcrylicCompositionKeepsTheTransparentSurface()
    {
        var plan = WindowBackdropService.CreatePlan(
            new Version(10, 0, 26100),
            highContrast: false,
            WindowBackdropKind.TransientWindow);

        Assert.False(WindowBackdropService.RequiresOpaqueFallback(
            plan,
            backdropResult: -1,
            frameResult: 0,
            accentResult: 1));
    }

    [Fact]
    public void AcrylicTintTracksTheWindowsTheme()
    {
        Assert.Equal(unchecked((int)0x99202020), WindowBackdropService.AcrylicTintColor(useDarkMode: true));
        Assert.Equal(unchecked((int)0xCCF7F7F7), WindowBackdropService.AcrylicTintColor(useDarkMode: false));
    }

    [Theory]
    [InlineData(0x0015)] // WM_SYSCOLORCHANGE
    [InlineData(0x007E)] // WM_DISPLAYCHANGE
    [InlineData(0x001A)] // WM_SETTINGCHANGE
    [InlineData(0x02E0)] // WM_DPICHANGED
    [InlineData(0x031A)] // WM_THEMECHANGED
    [InlineData(0x031E)] // WM_DWMCOMPOSITIONCHANGED
    [InlineData(0x0320)] // WM_DWMCOLORIZATIONCOLORCHANGED
    public void WindowsAppearanceMessagesReapplyTheBackdrop(int message)
    {
        Assert.True(WindowBackdropService.IsAppearanceChangeMessage(message));
    }

    [Fact]
    public void UnrelatedWindowMessageDoesNotReapplyTheBackdrop()
    {
        Assert.False(WindowBackdropService.IsAppearanceChangeMessage(0x0200)); // WM_MOUSEMOVE
    }

    private static void CompleteOpen(FlyoutTransitionController controller)
    {
        var open = controller.Show();
        Assert.True(open.Exists);
        Assert.True(controller.Complete(open));
        Assert.Equal(FlyoutPresentationState.Open, controller.State);
    }
}
