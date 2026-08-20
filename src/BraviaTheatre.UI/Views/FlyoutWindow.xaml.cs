using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.UI.Views;

public partial class FlyoutWindow : Window
{
    private readonly BraviaEngine _engine;
    private AppSettings _settings;
    private readonly Action _onOpenSettings;
    private bool _isUpdatingUi;
    private bool _isDraggingSlider;

    private static readonly SolidColorBrush GreenBrush = new((Color)ColorConverter.ConvertFromString("#44D644"));
    private static readonly SolidColorBrush BlueBrush = new((Color)ColorConverter.ConvertFromString("#4CC2FF"));
    private static readonly SolidColorBrush GrayBrush = new((Color)ColorConverter.ConvertFromString("#888888"));
    private static readonly SolidColorBrush LightGrayBrush = new((Color)ColorConverter.ConvertFromString("#D0D0D0"));
    private static readonly SolidColorBrush RedBrush = new((Color)ColorConverter.ConvertFromString("#E81123"));

    private static readonly Geometry SpeakerUnmutedGeometry = Geometry.Parse("M3,9v6h4l5,5V4L7,9H3z M16.5,12c0-1.77-1.02-3.29-2.5-4.03v8.05C15.48,15.29,16.5,13.77,16.5,12z M14,3.23v2.06c2.89,0.86,5,3.54,5,6.71s-2.11,5.85-5,6.71v2.06c4.01-0.91,7-4.49,7-8.77S18.01,4.14,14,3.23z");
    private static readonly Geometry SpeakerMutedGeometry = Geometry.Parse("M3,9v6h4l5,5V4L7,9H3z M21.71,7.71L20.29,6.29 17.5,9.09 14.71,6.29 13.29,7.71 16.09,10.5 13.29,13.29 14.71,14.71 17.5,11.91 20.29,14.71 21.71,13.29 18.91,10.5z");

    public FlyoutWindow(BraviaEngine engine, AppSettings settings, Action onOpenSettings)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _onOpenSettings = onOpenSettings;

        Deactivated += (s, e) => Hide();
        Loaded += (s, e) =>
        {
            ApplySettings(_settings);
            UpdateState(_engine.CurrentState);
        };

        SliderVolume.PreviewMouseDown += (s, e) => _isDraggingSlider = true;
        SliderVolume.PreviewMouseUp += (s, e) => _isDraggingSlider = false;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        PanelRearSpeaker.Visibility = _settings.ShowRearSpeaker ? Visibility.Visible : Visibility.Collapsed;
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
                if (state.Power)
                {
                    DotConnection.Fill = GreenBrush;
                    DotShadow.Color = (Color)ColorConverter.ConvertFromString("#44D644");
                    DotConnection.ToolTip = "Online (Connected)";
                }
                else
                {
                    DotConnection.Fill = GrayBrush;
                    DotShadow.Color = (Color)ColorConverter.ConvertFromString("#888888");
                    DotConnection.ToolTip = "Standby";
                }
            }
            else
            {
                DotConnection.Fill = RedBrush;
                DotShadow.Color = (Color)ColorConverter.ConvertFromString("#E81123");
                DotConnection.ToolTip = "Connecting / Offline";
            }

            // Input Function
            string fn = state.Function.ToUpperInvariant();
            BtnInputSource.Content = $"{fn} ▾";
            TxtInput.Text = $"{fn} / eARC";

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

            // Speaker / Mute icon update (Windows style mute icon in #D0D0D0)
            IconSpeaker.Data = state.Mute ? SpeakerMutedGeometry : SpeakerUnmutedGeometry;
            IconSpeaker.Fill = LightGrayBrush;

            UpdateBassPills(state.Bass);
            UpdateRearPills(state.RearLevel);

            TxtSoundFieldStatus.Text = state.SoundField ? "On" : "Off";
            TxtSoundFieldStatus.Foreground = state.SoundField ? BlueBrush : GrayBrush;

            TxtNightModeStatus.Text = state.NightMode ? "On" : "Off";
            TxtNightModeStatus.Foreground = state.NightMode ? BlueBrush : GrayBrush;

            TxtVoiceModeStatus.Text = state.VoiceMode ? "On" : "Off";
            TxtVoiceModeStatus.Foreground = state.VoiceMode ? BlueBrush : GrayBrush;

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
        ApplySegmentStyle(BtnBassMin, bass == "min");
        ApplySegmentStyle(BtnBassMid, bass == "mid");
        ApplySegmentStyle(BtnBassMax, bass == "max");
    }

    private void UpdateRearPills(int rear)
    {
        ApplySegmentStyle(BtnRearMin, rear < 0);
        ApplySegmentStyle(BtnRearMid, rear == 0);
        ApplySegmentStyle(BtnRearMax, rear > 0);
    }

    private void ApplySegmentStyle(Button btn, bool active)
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

    private void BtnSliderMute_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.ToggleMuteAsync();
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

    private void BtnRearMin_Click(object sender, RoutedEventArgs e)
    {
        UpdateRearPills(-6);
        _ = _engine.SetRearLevelAsync(-6);
    }

    private void BtnRearMid_Click(object sender, RoutedEventArgs e)
    {
        UpdateRearPills(0);
        _ = _engine.SetRearLevelAsync(0);
    }

    private void BtnRearMax_Click(object sender, RoutedEventArgs e)
    {
        UpdateRearPills(6);
        _ = _engine.SetRearLevelAsync(6);
    }

    private void BtnSoundField_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.ToggleSoundFieldAsync();
    }

    private void BtnNightMode_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.ToggleNightModeAsync();
    }

    private void BtnVoiceMode_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.ToggleVoiceModeAsync();
    }

    private void BtnPower_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.TogglePowerAsync();
    }

    private void BtnInputSource_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        string[] inputs = { "hdmi", "tv", "bluetooth", "spotify", "airplay" };

        foreach (var inp in inputs)
        {
            var item = new MenuItem
            {
                Header = inp.ToUpperInvariant(),
                IsChecked = string.Equals(_engine.CurrentState.Function, inp, StringComparison.OrdinalIgnoreCase)
            };
            string targetInp = inp;
            item.Click += (s, ev) => _ = _engine.SetFunctionAsync(targetInp);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = BtnInputSource;
        menu.IsOpen = true;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _onOpenSettings.Invoke();
    }
}
