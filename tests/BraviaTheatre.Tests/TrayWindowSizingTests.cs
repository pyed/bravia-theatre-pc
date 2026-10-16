using System.Windows;
using System.Windows.Controls;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Views;

namespace BraviaTheatre.Tests;

/// <summary>
/// The flyout came back three times as wide after the display's scale changed while the app ran
/// (100% with the reporter's TV off, 300% with it on). Its height followed the content and stayed
/// right; the fixed window width did not. Both windows now take their width from the content.
/// </summary>
public class TrayWindowSizingTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void FlyoutTakesItsWidthFromItsContent() => wpf.Run(() =>
    {
        using var engine = new BraviaEngine(new SonyCredentials(), "test-host");
        var flyout = new FlyoutWindow(engine, new AppSettings(), static () => { }, static () => { });
        try
        {
            Assert.Equal(SizeToContent.WidthAndHeight, flyout.SizeToContent);
            Assert.True(double.IsNaN(flyout.Width));
            Assert.Equal(384, Assert.IsType<Border>(flyout.FindName("RootBorder")).Width);
        }
        finally
        {
            flyout.CloseForShutdown();
        }
    });

    [Fact]
    public void TrayMenuTakesItsWidthFromItsContent() => wpf.Run(() =>
    {
        var menu = NewMenu();
        try
        {
            Assert.Equal(SizeToContent.WidthAndHeight, menu.SizeToContent);
            Assert.True(double.IsNaN(menu.Width));
            Assert.Equal(280, Assert.IsType<Border>(menu.FindName("RootBorder")).Width);
        }
        finally
        {
            menu.CloseForShutdown();
        }
    });

    [Fact]
    public void TrayMenuSurvivesACloseRequestAndOpensAgain() => wpf.Run(() =>
    {
        // Alt+F4 on the focused menu closed it for good, and the next right-click threw.
        var menu = NewMenu();
        try
        {
            menu.Show();
            menu.Close();
            Assert.False(menu.IsVisible);

            menu.Show();
            Assert.True(menu.IsVisible);
        }
        finally
        {
            menu.CloseForShutdown();
        }
    });

    private static TrayMenuWindow NewMenu() =>
        new(static () => { }, static () => { }, static () => { }, static () => { });
}
