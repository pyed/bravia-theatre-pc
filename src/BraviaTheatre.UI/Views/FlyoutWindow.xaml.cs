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

    // Centered 24x24 SVG geometries with exact alignment
    private static readonly Geometry SpeakerUnmutedGeometry = Geometry.Parse("M2,8v8h4l6,5V3L6,8H2z M16.5,8c.9,1.1 1.5,2.5 1.5,4s-.6,2.9-1.5,4l-1.5-1.5c.6-.7 1-1.6 1-2.5s-.4-1.8-1-2.5L16.5,8z M19,5.5c1.9,1.7 3,4 3,6.5s-1.1,4.8-3,6.5l-1.5-1.5c1.5-1.3 2.5-3.1 2.5-5s-1-3.7-2.5-5L19,5.5z");
    private static readonly Geometry SpeakerMutedGeometry = Geometry.Parse("M2,8v8h4l6,5V3L6,8H2z M15.4,9.4L18,12l-2.6,2.6 1.4,1.4 2.6-2.6 2.6,2.6 1.4-1.4L20.8,12l2.6-2.6-1.4-1.4-2.6,2.6-2.6-2.6-1.4,1.4z");

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
        if (IsVisible)
        {
            PositionNearTray();
        }
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
            PositionNearTray();
            Activate();
        }
    }

    public void PositionNearTray()
    {
        UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 410;
        Left = workArea.Right - w - 10;
        Top = workArea.Bottom - h - 10;
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

            // Glowing Power Button in header
            if (state.Connected)
            {
                if (state.Power)
                {
                    IconPower.Fill = GreenBrush;
                    PowerShadow.Color = (Color)ColorConverter.ConvertFromString("#44D644");
                    PowerShadow.Opacity = 0.85;
                    BtnHeaderPower.ToolTip = "Power: Online (Click for Standby)";
                }
                else
                {
                    IconPower.Fill = GrayBrush;
                    PowerShadow.Opacity = 0;
                    BtnHeaderPower.ToolTip = "Power: Standby (Click to Turn On)";
                }
            }
            else
            {
                IconPower.Fill = RedBrush;
                PowerShadow.Color = (Color)ColorConverter.ConvertFromString("#E81123");
                PowerShadow.Opacity = 0.85;
                BtnHeaderPower.ToolTip = "Connecting / Offline";
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

            SliderRear.Value = state.RearLevel;
            TxtRearValue.Text = state.RearLevel > 0 ? $"+{state.RearLevel}" : state.RearLevel.ToString();
            SliderRear.IsEnabled = state.Power;

            TxtSoundFieldStatus.Text = state.SoundField ? "On" : "Off";
            TxtSoundFieldStatus.Foreground = state.SoundField ? BlueBrush : GrayBrush;

            TxtNightModeStatus.Text = state.NightMode ? "On" : "Off";
            TxtNightModeStatus.Foreground = state.NightMode ? BlueBrush : GrayBrush;

            TxtVoiceModeStatus.Text = state.VoiceMode ? "On" : "Off";
            TxtVoiceModeStatus.Foreground = state.VoiceMode ? BlueBrush : GrayBrush;
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

    private void SliderRear_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || TxtRearValue == null || _engine == null) return;
        int val = (int)Math.Round(e.NewValue);
        TxtRearValue.Text = val > 0 ? $"+{val}" : val.ToString();
        _ = _engine.SetRearLevelAsync(val);
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
        string[] inputs = { "hdmi", "tv", "bluetooth" };

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
