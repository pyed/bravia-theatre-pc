using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

public class TrayAttentionNotifierTests
{
    private static readonly SoundbarState Connected = SoundbarState.Disconnected with { Connected = true, Power = true };
    private static readonly SoundbarState SignInNeeded = SoundbarState.Disconnected with { AuthRequired = true };
    private static readonly SoundbarState SoundbarChoiceNeeded = SoundbarState.Disconnected with { DeviceAssociationRequired = true };

    [Fact]
    public void SignInIsAnnouncedOnceWhileTheEngineKeepsReportingIt()
    {
        var notifier = new TrayAttentionNotifier();

        Assert.Null(notifier.Update(Connected));
        Assert.Equal(TrayAttention.SignIn, notifier.Update(SignInNeeded)?.Attention);

        // The engine republishes the same state on every retry while it waits.
        Assert.Null(notifier.Update(SignInNeeded));
        Assert.Null(notifier.Update(SignInNeeded with { Volume = 1 }));
    }

    [Fact]
    public void ANewNeedAfterRecoveryIsAnnouncedAgain()
    {
        var notifier = new TrayAttentionNotifier();

        Assert.NotNull(notifier.Update(SignInNeeded));
        Assert.Null(notifier.Update(Connected));
        Assert.Equal(TrayAttention.SignIn, notifier.Update(SignInNeeded)?.Attention);
    }

    [Fact]
    public void ChoosingTheSoundbarIsAnnouncedAndDiffersFromSigningIn()
    {
        var notifier = new TrayAttentionNotifier();

        var choose = notifier.Update(SoundbarChoiceNeeded);
        var signIn = notifier.Update(SignInNeeded);

        Assert.Equal(TrayAttention.ChooseSoundbar, choose?.Attention);
        Assert.Equal(TrayAttention.SignIn, signIn?.Attention);
        Assert.NotEqual(choose?.Title, signIn?.Title);
    }

    [Fact]
    public void TextFitsTheShellLimits()
    {
        foreach (var state in new[] { SignInNeeded, SoundbarChoiceNeeded })
        {
            var notification = new TrayAttentionNotifier().Update(state);

            Assert.NotNull(notification);
            Assert.InRange(notification.Value.Title.Length, 1, 63);
            Assert.InRange(notification.Value.Text.Length, 1, 255);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectingTheNotificationIsRecognised(bool usesVersion4)
    {
        Assert.Equal(
            NativeTrayIcon.TrayCallbackAction.NotificationClicked,
            NativeTrayIcon.ClassifyCallback(0x0405, usesVersion4)); // NIN_BALLOONUSERCLICK
    }

    [Fact]
    public void NotificationsRespectQuietTime()
    {
        Assert.NotEqual(0, NativeTrayIcon.NotificationFlags & 0x00000080); // NIIF_RESPECT_QUIET_TIME
    }
}
