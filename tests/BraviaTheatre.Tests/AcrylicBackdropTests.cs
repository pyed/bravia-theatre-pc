using System.Windows;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

/// <summary>
/// After days of sleep and resume cycles the flyout opened fully transparent and without its edge:
/// DWM had dropped its acrylic material, and the app kept applying the same accent, which Windows
/// ignores. The accent is now on only while a transient window is shown, so each show is a change
/// that makes DWM build the material again.
/// </summary>
public class AcrylicBackdropTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void TransientWindowHasTheAcrylicAccentOnlyWhileShown()
    {
        var plan = WindowBackdropService.CreatePlan(
            Environment.OSVersion.Version,
            SystemParameters.HighContrast,
            WindowBackdropKind.TransientWindow);
        Assert.SkipUnless(plan.UseAcrylicComposition, "This Windows build or theme uses no acrylic accent.");

        wpf.Run(() =>
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Width = 120,
                Height = 80,
                Left = 0,
                Top = 0
            };
            WindowBackdropService.Attach(window, WindowBackdropKind.TransientWindow);
            try
            {
                window.Show();
                Assert.Equal(WindowBackdropService.AccentEnableAcrylicBlurBehind, WindowBackdropService.AppliedAccentState(window));

                window.Hide();
                Assert.Equal(WindowBackdropService.AccentDisabled, WindowBackdropService.AppliedAccentState(window));

                // Refreshes that run while hidden, such as after a display change, leave it off.
                WindowBackdropService.Refresh(window);
                Assert.Equal(WindowBackdropService.AccentDisabled, WindowBackdropService.AppliedAccentState(window));

                window.Show();
                Assert.Equal(WindowBackdropService.AccentEnableAcrylicBlurBehind, WindowBackdropService.AppliedAccentState(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(0x0046, 0x0040u, true)] // WM_WINDOWPOSCHANGING, SWP_SHOWWINDOW: before it appears
    [InlineData(0x0047, 0x0080u, false)] // WM_WINDOWPOSCHANGED, SWP_HIDEWINDOW: once it is gone
    [InlineData(0x0047, 0x0040u, null)] // already switched on before it appeared
    [InlineData(0x0046, 0x0080u, null)] // still on screen; switching off here could show a frame without it
    [InlineData(0x0046, 0x0017u, null)] // the flyout's slide animation moves it every frame
    [InlineData(0x0047, 0x0017u, null)]
    [InlineData(0x0200, 0x0040u, null)] // not a window-position message
    public void AccentFollowsTheWindowBeingShownAndHidden(int message, uint flags, bool? expected)
    {
        Assert.Equal(expected, WindowBackdropService.AcrylicAccentForWindowPosition(message, flags));
    }
}
