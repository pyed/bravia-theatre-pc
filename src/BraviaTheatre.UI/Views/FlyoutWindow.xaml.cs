using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.UI.Views;

public partial class FlyoutWindow : Window
{
    private readonly BraviaEngine _engine;
    private bool _isUpdatingUi;

    private static readonly SolidColorBrush GreenBrush = new((Color)ColorConverter.ConvertFromString("#44D644"));
    private static readonly SolidColorBrush BlueBrush = new((Color)ColorConverter.ConvertFromString("#0078D7"));
    private static readonly SolidColorBrush GrayBrush = new((Color)ColorConverter.ConvertFromString("#888888"));
    private static readonly SolidColorBrush RedBrush = new((Color)ColorConverter.ConvertFromString("#E81123"));

    public FlyoutWindow(BraviaEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        Deactivated += (s, e) => Hide();
        Loaded += (s, e) => UpdateState(_engine.CurrentState);
    }

    public void ToggleFlyout()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            PositionNearTray();
            Show();
            Activate();
        }
    }

    public void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 10;
        Top = workArea.Bottom - Height - 10;
    }

    public void UpdateState(SoundbarState state)
    {
        Dispatcher.Invoke(() =>
        {
            _isUpdatingUi = true;
            try
            {
                TxtDeviceName.Text = state.DeviceName ?? "BRAVIA Theatre Bar 9";

                if (state.Connected)
                {
                    TxtConnection.Text = state.Power ? "Online" : "Standby";
                    TxtConnection.Foreground = state.Power ? GreenBrush : GrayBrush;
                    BadgeConnection.Background = state.Power ? new SolidColorBrush(Color.FromArgb(40, 68, 214, 68)) : new SolidColorBrush(Color.FromArgb(40, 136, 136, 136));
                }
                else
                {
                    TxtConnection.Text = "Connecting...";
                    TxtConnection.Foreground = RedBrush;
                    BadgeConnection.Background = new SolidColorBrush(Color.FromArgb(40, 232, 17, 35));
                }

                ImgCodecBadge.Source = IconHelper.GetImageSource(state.CodecBadgeKind);
                TxtCodecName.Text = state.HumanCodec;

                if (!string.IsNullOrEmpty(state.Channel))
                {
                    BadgeChannel.Visibility = Visibility.Visible;
                    TxtChannel.Text = state.Channel.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ? state.Channel : $"{state.Channel} ch";
                }
                else
                {
                    BadgeChannel.Visibility = Visibility.Collapsed;
                }

                SliderVolume.Value = state.Volume;
                TxtVolumeValue.Text = state.Volume.ToString();
                SliderVolume.IsEnabled = state.Power;

                TxtSoundFieldStatus.Text = state.SoundField ? "On" : "Off";
                TxtSoundFieldStatus.Foreground = state.SoundField ? BlueBrush : GrayBrush;

                TxtNightModeStatus.Text = state.NightMode ? "On" : "Off";
                TxtNightModeStatus.Foreground = state.NightMode ? BlueBrush : GrayBrush;

                TxtMuteStatus.Text = state.Mute ? "Muted" : "Unmuted";
                TxtMuteStatus.Foreground = state.Mute ? RedBrush : GrayBrush;

                TxtPowerStatus.Text = state.Power ? "On" : "Standby";
                TxtPowerStatus.Foreground = state.Power ? GreenBrush : GrayBrush;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        });
    }

    private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi) return;
        int vol = (int)Math.Round(e.NewValue);
        TxtVolumeValue.Text = vol.ToString();
        _ = _engine.SetVolumeAsync(vol);
    }

    private void BtnSoundField_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.ToggleSoundFieldAsync();
    }

    private void BtnNightMode_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.ToggleNightModeAsync();
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.ToggleMuteAsync();
    }

    private void BtnPower_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.TogglePowerAsync();
    }
}
