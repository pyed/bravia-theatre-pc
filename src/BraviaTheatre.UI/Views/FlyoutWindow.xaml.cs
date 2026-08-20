using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private bool _isDraggingSlider;

    private static readonly SolidColorBrush GreenBrush = new((Color)ColorConverter.ConvertFromString("#44D644"));
    private static readonly SolidColorBrush BlueBrush = new((Color)ColorConverter.ConvertFromString("#4CC2FF"));
    private static readonly SolidColorBrush GrayBrush = new((Color)ColorConverter.ConvertFromString("#888888"));
    private static readonly SolidColorBrush LightGrayBrush = new((Color)ColorConverter.ConvertFromString("#D0D0D0"));
    private static readonly SolidColorBrush RedBrush = new((Color)ColorConverter.ConvertFromString("#E81123"));

    public FlyoutWindow(BraviaEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        Deactivated += (s, e) => Hide();
        Loaded += (s, e) => UpdateState(_engine.CurrentState);

        SliderVolume.PreviewMouseDown += (s, e) => _isDraggingSlider = true;
        SliderVolume.PreviewMouseUp += (s, e) => _isDraggingSlider = false;
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateState(state));
            return;
        }

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

            if (!_isDraggingSlider)
            {
                SliderVolume.Value = state.Volume;
                TxtVolumeValue.Text = state.Volume.ToString();
            }
            SliderVolume.IsEnabled = state.Power;

            IconSpeaker.Fill = state.Mute ? RedBrush : LightGrayBrush;

            UpdateBassPills(state.Bass);

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
    }

    private void UpdateBassPills(string bass)
    {
        bass = bass.ToLowerInvariant();
        ApplyBassBtnStyle(BtnBassMin, bass == "min");
        ApplyBassBtnStyle(BtnBassMid, bass == "mid");
        ApplyBassBtnStyle(BtnBassMax, bass == "max");
    }

    private void ApplyBassBtnStyle(Button btn, bool active)
    {
        if (btn == null) return;
        btn.Background = active ? BlueBrush : Brushes.Transparent;
        btn.Foreground = active ? Brushes.Black : GrayBrush;
    }

    private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || TxtVolumeValue == null || _engine == null) return;
        int vol = (int)Math.Round(e.NewValue);
        TxtVolumeValue.Text = vol.ToString();
        _ = _engine.SetVolumeAsync(vol);
    }

    private void BorderVolume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_engine == null) return;
        int step = e.Delta > 0 ? 1 : -1;
        int target = Math.Clamp(_engine.CurrentState.Volume + step, 0, 100);
        SliderVolume.Value = target;
        TxtVolumeValue.Text = target.ToString();
        _ = _engine.SetVolumeAsync(target);
    }

    private void BtnBassMin_Click(object sender, RoutedEventArgs e)
    {
        UpdateBassPills("min");
        _ = _engine.SetBassAsync("min");
    }

    private void BtnBassMid_Click(object sender, RoutedEventArgs e)
    {
        UpdateBassPills("mid");
        _ = _engine.SetBassAsync("mid");
    }

    private void BtnBassMax_Click(object sender, RoutedEventArgs e)
    {
        UpdateBassPills("max");
        _ = _engine.SetBassAsync("max");
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
