using BraviaTheatre.Core.Models;

namespace BraviaTheatre.UI.Services;

internal enum TrayAttention
{
    None,
    SignIn,
    ChooseSoundbar
}

internal readonly record struct TrayNotification(TrayAttention Attention, string Title, string Text);

/// <summary>
/// Decides when the tray tells the user that the app cannot continue without them.
/// </summary>
/// <remarks>
/// Only the flyout card and the tooltip used to change, so a tray app running for days could
/// stop controlling the soundbar without anyone noticing. The engine keeps reporting the same
/// state while it waits, so this notifies once per change, not on every report.
/// </remarks>
internal sealed class TrayAttentionNotifier
{
    private TrayAttention _current;

    public TrayNotification? Update(SoundbarState state)
    {
        var attention = state.AuthRequired
            ? TrayAttention.SignIn
            : state.DeviceAssociationRequired
                ? TrayAttention.ChooseSoundbar
                : TrayAttention.None;
        if (attention == _current) return null;

        _current = attention;
        return attention switch
        {
            TrayAttention.SignIn => new TrayNotification(
                attention,
                "Sony sign-in needed",
                "Sign in to your Sony account again to keep controlling your soundbar. Select this to open BRAVIA Theatre PC."),
            TrayAttention.ChooseSoundbar => new TrayNotification(
                attention,
                "Choose your soundbar",
                "Your soundbar's link in BRAVIA Connect changed. Select this to choose it again and reconnect."),
            _ => null
        };
    }
}
