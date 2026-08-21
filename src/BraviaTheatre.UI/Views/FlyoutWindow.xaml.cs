using System;
using System.ComponentModel;
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
    private bool _allowClose;

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
            Opacity = 0;
            FlyoutTransform.Y = 12;
            Show();
            Activate();

            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            var slideUp = new System.Windows.Media.Animation.DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            BeginAnimation(OpacityProperty, fadeIn);
            FlyoutTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
    }

    public void ShowFlyout()
    {
        if (IsVisible)
        {
            Activate();
            return;
        }
        ToggleFlyout();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Add or Key.VolumeUp)
        {
            int newVol = Math.Clamp(_engine.CurrentState.Volume + 1, 0, 100);
            _ = _engine.SetVolumeAsync(newVol);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Subtract or Key.VolumeDown)
        {
            int newVol = Math.Clamp(_engine.CurrentState.Volume - 1, 0, 100);
            _ = _engine.SetVolumeAsync(newVol);
            e.Handled = true;
        }
        else if (e.Key is Key.M or Key.VolumeMute)
        {
            _ = _engine.ToggleMuteAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            _ = _engine.ToggleNightModeAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            _ = _engine.ToggleSoundFieldAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            _ = _engine.ToggleVoiceModeAsync();
            e.Handled = true;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public void PositionNearTray()
    {
        UpdateLayout();
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 410;

        try
        {
            if (GetCursorPos(out var cursorPt))
            {
                var hMon = MonitorFromPoint(cursorPt, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                    double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

                    double workLeft = mi.rcWork.Left / dpiX;
                    double workRight = mi.rcWork.Right / dpiX;
                    double workTop = mi.rcWork.Top / dpiY;
                    double workBottom = mi.rcWork.Bottom / dpiY;

                    Left = Math.Max(workLeft + 10, workRight - w - 10);
                    Top = Math.Max(workTop + 10, workBottom - h - 10);
                    return;
                }
            }
        }
        catch { }

        // Fallback to primary work area
        var workArea = SystemParameters.WorkArea;
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
            TxtInput.Text = state.Function.ToLowerInvariant() switch
            {
                "tv" => "TV / eARC",
                "bluetooth" => "Bluetooth",
                "hdmi" => "HDMI",
                _ => fn
            };

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
            var connectedAndPowered = state.Connected && state.Power;
            BtnHeaderPower.IsEnabled = state.Connected;
            BtnInputSource.IsEnabled = connectedAndPowered;
            BtnSliderMute.IsEnabled = connectedAndPowered;
            BtnBassMin.IsEnabled = connectedAndPowered;
            BtnBassMid.IsEnabled = connectedAndPowered;
            BtnBassMax.IsEnabled = connectedAndPowered;
            BtnSoundField.IsEnabled = connectedAndPowered;
            BtnNightMode.IsEnabled = connectedAndPowered;
            BtnVoiceMode.IsEnabled = connectedAndPowered;
            SliderVolume.IsEnabled = connectedAndPowered;

            // Speaker / Mute icon update (Windows style mute icon in #D0D0D0)
            IconSpeaker.Data = state.Mute ? SpeakerMutedGeometry : SpeakerUnmutedGeometry;
            IconSpeaker.Fill = LightGrayBrush;

            UpdateBassPills(state.Bass);

            SliderRear.Value = state.RearLevel;
            TxtRearValue.Text = state.RearLevel > 0 ? $"+{state.RearLevel}" : state.RearLevel.ToString();
            SliderRear.IsEnabled = connectedAndPowered;

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
