using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.UI.Views;

public partial class FlyoutWindow : Window
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly BraviaEngine _engine;
    private readonly Action _onAuthenticate;
    private readonly FlyoutTransitionController _presentation = new();
    private AppSettings _settings;
    private DispatcherTimer? _animationTimer;
    private PixelRect? _trayAnchor;
    private FlyoutPlacement _placement;
    private PixelPoint? _lastDeactivationCursor;
    private uint _lastDeactivationMessageTime;
    private bool _inputMenuOpen;
    private bool _deactivatedWhileInputMenuOpen;
    private bool _authenticateAfterClose;
    private bool _isUpdatingUi;
    private bool _isDraggingSlider;
    private bool _allowClose;

    private static readonly Geometry SpeakerUnmutedGeometry = Geometry.Parse(
        "M2,8v8h4l6,5V3L6,8H2z M16.5,8c.9,1.1 1.5,2.5 1.5,4s-.6,2.9-1.5,4l-1.5-1.5c.6-.7 1-1.6 1-2.5s-.4-1.8-1-2.5L16.5,8z M19,5.5c1.9,1.7 3,4 3,6.5s-1.1,4.8-3,6.5l-1.5-1.5c1.5-1.3 2.5-3.1 2.5-5s-1-3.7-2.5-5L19,5.5z");
    private static readonly Geometry SpeakerMutedGeometry = Geometry.Parse(
        "M2,8v8h4l6,5V3L6,8H2z M15.4,9.4L18,12l-2.6,2.6 1.4,1.4 2.6-2.6 2.6,2.6 1.4-1.4L20.8,12l2.6-2.6-1.4-1.4-2.6,2.6-2.6-2.6-1.4,1.4z");

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative WorkArea;
        public uint Flags;
    }

    public FlyoutWindow(BraviaEngine engine, AppSettings settings, Action onAuthenticate)
    {
        InitializeComponent();
        _engine = engine;
        _settings = settings;
        _onAuthenticate = onAuthenticate;

        WindowBackdropService.Attach(this, WindowBackdropKind.TransientWindow);
        Deactivated += OnDeactivated;
        Loaded += (_, _) =>
        {
            ApplySettings(_settings);
            UpdateState(_engine.CurrentState);
        };

        SliderVolume.PreviewMouseDown += (_, _) => _isDraggingSlider = true;
        SliderVolume.PreviewMouseUp += (_, _) => _isDraggingSlider = false;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        PanelRearSpeaker.Visibility = _settings.ShowRearSpeaker ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible)
            Dispatcher.BeginInvoke(RepositionIfVisible, DispatcherPriority.Loaded);
    }

    public void ToggleFlyout()
    {
        var transition = _presentation.ToggleFromTray(sameDeactivationInteraction: false);
        if (!transition.Exists) return;
        if (transition.Target == FlyoutPresentationState.Opening)
            BeginOpen(transition, null);
        else
            BeginClose(transition);
    }

    internal void ToggleFromTray(TrayActivation activation, PixelRect? trayAnchor)
    {
        var sameDeactivationInteraction = TrayInteractionCorrelator.IsSameInteraction(
            activation,
            _lastDeactivationMessageTime,
            _lastDeactivationCursor,
            trayAnchor,
            _presentation.IsAwaitingCorrelatedTrayToggle);
        var transition = _presentation.ToggleFromTray(sameDeactivationInteraction);
        if (!transition.Exists) return;

        if (transition.Target == FlyoutPresentationState.Opening)
            BeginOpen(transition, trayAnchor);
        else
            BeginClose(transition);
    }

    public void ShowFlyout() => ShowFlyout(null);

    internal void ShowFlyout(PixelRect? trayAnchor)
    {
        _trayAnchor = trayAnchor ?? _trayAnchor;
        var transition = _presentation.Show();
        if (!transition.Exists)
        {
            if (_presentation.State == FlyoutPresentationState.Open)
                RepositionIfVisible(trayAnchor);
            Activate();
            return;
        }

        BeginOpen(transition, trayAnchor);
    }

    public void RepositionIfVisible() => RepositionIfVisible(null);

    internal void RepositionIfVisible(PixelRect? trayAnchor)
    {
        _trayAnchor = trayAnchor ?? _trayAnchor;
        if (!IsVisible || _presentation.State != FlyoutPresentationState.Open)
            return;

        _placement = ResolvePlacement(_trayAnchor);
        SetWindowPosition(_placement.Bounds.Left, _placement.Bounds.Top);
    }

    public void PositionNearTray() => RepositionIfVisible();

    internal void ResolveTrayInteraction() => _presentation.ResolveTrayInteraction();

    public void CloseForShutdown()
    {
        _allowClose = true;
        _authenticateAfterClose = false;
        StopAnimation();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            RequestClose(causedByDeactivation: false);
        }
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.Escape)
        {
            RequestClose(causedByDeactivation: false);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Add or Key.VolumeUp)
        {
            if (!SliderVolume.IsEnabled) return;
            SliderVolume.Value = Math.Clamp(Math.Round(SliderVolume.Value) + 1, 0, 100);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Subtract or Key.VolumeDown)
        {
            if (!SliderVolume.IsEnabled) return;
            SliderVolume.Value = Math.Clamp(Math.Round(SliderVolume.Value) - 1, 0, 100);
            e.Handled = true;
        }
        else if (e.Key is Key.M or Key.VolumeMute)
        {
            if (!BtnSliderMute.IsEnabled) return;
            _ = _engine.ToggleMuteAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            if (!BtnNightMode.IsEnabled) return;
            _ = _engine.ToggleNightModeAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            if (!BtnSoundField.IsEnabled) return;
            _ = _engine.ToggleSoundFieldAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            if (!BtnVoiceMode.IsEnabled) return;
            _ = _engine.ToggleVoiceModeAsync();
            e.Handled = true;
        }
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
            var presentation = SoundbarUiPresentationFactory.Create(state);
            TxtDeviceName.Text = state.DeviceName ?? "BRAVIA Theatre";
            AuthRequiredCard.Visibility = presentation.ShowAuthenticationPrompt
                ? Visibility.Visible
                : Visibility.Collapsed;
            CodecCard.Visibility = presentation.ShowAuthenticationPrompt
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (state.AuthRequired)
            {
                SetBrushResource(IconPower, Shape.FillProperty, "SystemFillColorCautionBrush");
                BtnHeaderPower.ToolTip = "Sony account sign-in required";
            }
            else if (!state.Connected)
            {
                SetBrushResource(IconPower, Shape.FillProperty, "SystemFillColorCriticalBrush");
                BtnHeaderPower.ToolTip = "Connecting / Offline";
            }
            else if (state.Power)
            {
                SetBrushResource(IconPower, Shape.FillProperty, "SystemFillColorSuccessBrush");
                BtnHeaderPower.ToolTip = "Power: On (click for standby)";
            }
            else
            {
                SetBrushResource(IconPower, Shape.FillProperty, "TextFillColorTertiaryBrush");
                BtnHeaderPower.ToolTip = "Power: Standby (click to turn on)";
            }

            var function = string.IsNullOrWhiteSpace(state.Function) ? "HDMI" : state.Function;
            var normalizedFunction = function.ToUpperInvariant();
            TxtHeaderInput.Text = normalizedFunction;
            TxtInput.Text = function.ToLowerInvariant() switch
            {
                "tv" => "TV / eARC",
                "bluetooth" => "Bluetooth",
                "hdmi" => "HDMI",
                _ => normalizedFunction
            };
            AutomationProperties.SetName(BtnInputSource, $"Input source: {TxtInput.Text}");

            ImgCodecBadge.Source = IconHelper.GetImageSource(state.CodecBadgeKind);
            TxtCodecName.Text = state.HumanCodec;

            if (!string.IsNullOrEmpty(state.Channel))
            {
                BadgeChannel.Visibility = Visibility.Visible;
                TxtChannel.Text = state.Channel.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
                    ? state.Channel
                    : $"{state.Channel} ch";
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

            var connectedAndPowered = presentation.ControlsEnabled;
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

            AutomationProperties.SetName(
                BtnHeaderPower,
                state.AuthRequired
                    ? "Soundbar power unavailable: Sony account sign-in required"
                    : !state.Connected
                    ? "Soundbar power: offline"
                    : state.Power ? "Soundbar power: on" : "Soundbar power: standby");
            AutomationProperties.SetName(
                BtnSliderMute,
                state.Mute ? "Unmute (currently muted)" : "Mute (currently unmuted)");

            IconSpeaker.Data = state.Mute ? SpeakerMutedGeometry : SpeakerUnmutedGeometry;
            UpdateBassPills(state.Bass);

            SliderRear.Value = state.RearLevel;
            TxtRearValue.Text = state.RearLevel > 0 ? $"+{state.RearLevel}" : state.RearLevel.ToString();
            SliderRear.IsEnabled = connectedAndPowered;

            BtnSoundField.IsChecked = state.SoundField;
            BtnNightMode.IsChecked = state.NightMode;
            BtnVoiceMode.IsChecked = state.VoiceMode;
            TxtSoundFieldStatus.Text = state.SoundField ? "On" : "Off";
            TxtNightModeStatus.Text = state.NightMode ? "On" : "Off";
            TxtVoiceModeStatus.Text = state.VoiceMode ? "On" : "Off";
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_allowClose) return;

        CaptureDeactivationContext();

        if (_inputMenuOpen)
        {
            _deactivatedWhileInputMenuOpen = true;
            return;
        }

        RequestClose(causedByDeactivation: true);
    }

    private void CaptureDeactivationContext()
    {
        _lastDeactivationCursor = null;
        if (GetCursorPos(out var cursor))
            _lastDeactivationCursor = new PixelPoint(cursor.X, cursor.Y);
        _lastDeactivationMessageTime = unchecked((uint)GetMessageTime());
    }

    private void RequestClose(bool causedByDeactivation)
    {
        var transition = _presentation.Hide(causedByDeactivation);
        if (transition.Exists) BeginClose(transition);
    }

    private void BeginOpen(FlyoutTransition transition, PixelRect? trayAnchor)
    {
        StopAnimation();
        _authenticateAfterClose = false;
        _trayAnchor = trayAnchor ?? _trayAnchor;
        var wasVisible = IsVisible;

        if (!wasVisible)
        {
            RootBorder.Opacity = 0;
            Show();
        }

        UpdateLayout();
        _placement = wasVisible
            ? CalculatePlacement(_trayAnchor)
            : ResolvePlacement(_trayAnchor);
        var target = _placement.Bounds;
        var start = wasVisible && TryGetWindowBounds(out var current)
            ? current
            : OffsetTowardTaskbar(target, _placement.TaskbarEdge, GetAnimationOffset());

        SetWindowPosition(start.Left, start.Top);
        Activate();
        WindowBackdropService.Refresh(this);
        if (!_presentation.IsCurrent(transition)) return;
        StartAnimation(transition, start, target, RootBorder.Opacity, 1, TimeSpan.FromMilliseconds(170), easeOut: true);
    }

    private void BeginClose(FlyoutTransition transition)
    {
        StopAnimation();
        if (!IsVisible || !TryGetWindowBounds(out var start))
        {
            FinishTransition(transition);
            return;
        }

        var target = OffsetTowardTaskbar(start, _placement.TaskbarEdge, GetAnimationOffset());
        StartAnimation(transition, start, target, RootBorder.Opacity, 0, TimeSpan.FromMilliseconds(125), easeOut: false);
    }

    private void StartAnimation(
        FlyoutTransition transition,
        PixelRect start,
        PixelRect target,
        double startOpacity,
        double targetOpacity,
        TimeSpan duration,
        bool easeOut)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            SetWindowPosition(target.Left, target.Top);
            RootBorder.Opacity = targetOpacity;
            FinishTransition(transition);
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animationTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (!_presentation.IsCurrent(transition))
            {
                StopAnimation(timer);
                return;
            }

            var linearProgress = Math.Clamp(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var easedProgress = easeOut
                ? 1 - Math.Pow(1 - linearProgress, 3)
                : Math.Pow(linearProgress, 3);

            var left = Interpolate(start.Left, target.Left, easedProgress);
            var top = Interpolate(start.Top, target.Top, easedProgress);
            SetWindowPosition(left, top);
            RootBorder.Opacity = startOpacity + ((targetOpacity - startOpacity) * easedProgress);

            if (linearProgress >= 1)
            {
                StopAnimation(timer);
                FinishTransition(transition);
            }
        };
        timer.Start();
    }

    private void FinishTransition(FlyoutTransition transition)
    {
        if (!_presentation.Complete(transition)) return;

        if (_presentation.State == FlyoutPresentationState.Open)
        {
            _placement = ResolvePlacement(_trayAnchor);
            SetWindowPosition(_placement.Bounds.Left, _placement.Bounds.Top);
            RootBorder.Opacity = 1;
            WindowBackdropService.Refresh(this);
        }
        else if (_presentation.State == FlyoutPresentationState.Hidden)
        {
            RootBorder.Opacity = 0;
            Hide();
            RootBorder.Opacity = 1;
            if (_authenticateAfterClose && !_allowClose &&
                !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _authenticateAfterClose = false;
                Dispatcher.BeginInvoke(_onAuthenticate, DispatcherPriority.Background);
            }
        }
    }

    private void StopAnimation()
    {
        var timer = _animationTimer;
        _animationTimer = null;
        timer?.Stop();
    }

    private void StopAnimation(DispatcherTimer timer)
    {
        timer.Stop();
        if (ReferenceEquals(_animationTimer, timer))
            _animationTimer = null;
    }

    private FlyoutPlacement CalculatePlacement(PixelRect? requestedAnchor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var anchor = requestedAnchor ?? GetCursorAnchor();
        var monitorPoint = new PointNative { X = anchor.CenterX, Y = anchor.CenterY };
        var monitorHandle = MonitorFromPoint(monitorPoint, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (monitorHandle != IntPtr.Zero &&
            GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            var dpi = Math.Max(96u, GetDpiForWindow(hwnd));
            var margin = (int)Math.Round(12 * dpi / 96d);
            var maximumWidth = Math.Max(
                1,
                (monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left - (2 * margin)) * 96d / dpi);
            var maximumHeight = Math.Max(
                1,
                (monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top - (2 * margin)) * 96d / dpi);
            if (Math.Abs(MaxWidth - maximumWidth) > 0.5 || Math.Abs(MaxHeight - maximumHeight) > 0.5)
            {
                MaxWidth = maximumWidth;
                MaxHeight = maximumHeight;
                UpdateLayout();
            }

            if (GetWindowRect(hwnd, out var windowRect))
            {
                return FlyoutPlacementCalculator.Calculate(
                    ToPixelRect(monitorInfo.Monitor),
                    ToPixelRect(monitorInfo.WorkArea),
                    anchor,
                    Math.Max(1, windowRect.Right - windowRect.Left),
                    Math.Max(1, windowRect.Bottom - windowRect.Top),
                    margin);
            }
        }

        var fallback = SystemParameters.WorkArea;
        MaxWidth = Math.Max(1, fallback.Width - 24);
        MaxHeight = Math.Max(1, fallback.Height - 24);
        UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth > 0 ? ActualWidth : Width));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight > 0 ? ActualHeight : 480));
        var work = new PixelRect((int)fallback.Left, (int)fallback.Top, (int)fallback.Right, (int)fallback.Bottom);
        return FlyoutPlacementCalculator.Calculate(work, work, anchor, width, height, 12);
    }

    private FlyoutPlacement ResolvePlacement(PixelRect? requestedAnchor)
    {
        var initial = CalculatePlacement(requestedAnchor);
        SetWindowPosition(initial.Bounds.Left, initial.Bounds.Top);
        UpdateLayout();
        return CalculatePlacement(requestedAnchor);
    }

    private static PixelRect GetCursorAnchor()
    {
        return GetCursorPos(out var cursor)
            ? PixelRect.FromPositionAndSize(cursor.X, cursor.Y, 1, 1)
            : PixelRect.FromPositionAndSize(0, 0, 1, 1);
    }

    private bool TryGetWindowBounds(out PixelRect bounds)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            bounds = ToPixelRect(rect);
            return true;
        }

        bounds = default;
        return false;
    }

    private void SetWindowPosition(int left, int top)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private int GetAnimationOffset()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = hwnd == IntPtr.Zero ? 96u : Math.Max(96u, GetDpiForWindow(hwnd));
        return Math.Max(8, (int)Math.Round(12 * dpi / 96d));
    }

    private static PixelRect OffsetTowardTaskbar(PixelRect rect, TaskbarEdge edge, int offset) => edge switch
    {
        TaskbarEdge.Left => PixelRect.FromPositionAndSize(rect.Left - offset, rect.Top, rect.Width, rect.Height),
        TaskbarEdge.Top => PixelRect.FromPositionAndSize(rect.Left, rect.Top - offset, rect.Width, rect.Height),
        TaskbarEdge.Right => PixelRect.FromPositionAndSize(rect.Left + offset, rect.Top, rect.Width, rect.Height),
        _ => PixelRect.FromPositionAndSize(rect.Left, rect.Top + offset, rect.Width, rect.Height)
    };

    private static PixelRect ToPixelRect(RectNative rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static int Interpolate(int start, int end, double progress) =>
        (int)Math.Round(start + ((end - start) * progress));

    private static void SetBrushResource(FrameworkElement element, DependencyProperty property, string resourceKey) =>
        element.SetResourceReference(property, resourceKey);

    private void UpdateBassPills(string bass)
    {
        bass = bass.ToLowerInvariant();
        ApplySegmentStyle(BtnBassMin, "Minimum bass", bass == "min");
        ApplySegmentStyle(BtnBassMid, "Medium bass", bass == "mid");
        ApplySegmentStyle(BtnBassMax, "Maximum bass", bass == "max");
    }

    private static void ApplySegmentStyle(Button button, string label, bool active)
    {
        button.Tag = active;
        button.SetResourceReference(
            Control.BackgroundProperty,
            active ? "AccentFillColorDefaultBrush" : "ControlFillColorTransparentBrush");
        button.SetResourceReference(
            Control.ForegroundProperty,
            active ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(button, active ? $"{label}, selected" : label);
    }

    private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || _isUpdatingUi || TxtVolumeValue == null) return;
        var volume = (int)Math.Round(e.NewValue);
        TxtVolumeValue.Text = volume.ToString();
        _ = _engine.SetVolumeAsync(volume);
    }

    private void SliderRear_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || _isUpdatingUi || TxtRearValue == null) return;
        var value = (int)Math.Round(e.NewValue);
        TxtRearValue.Text = value > 0 ? $"+{value}" : value.ToString();
        _ = _engine.SetRearLevelAsync(value);
    }

    private void BorderVolume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!SliderVolume.IsEnabled || e.Delta == 0) return;

        e.Handled = true;
        var step = e.Delta > 0 ? 1 : -1;
        var target = Math.Clamp((int)Math.Round(SliderVolume.Value) + step, 0, 100);
        SliderVolume.Value = target;
    }

    private void BtnSliderMute_Click(object sender, RoutedEventArgs e) => _ = _engine.ToggleMuteAsync();

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

    private void BtnSoundField_Click(object sender, RoutedEventArgs e) => _ = _engine.ToggleSoundFieldAsync();

    private void BtnNightMode_Click(object sender, RoutedEventArgs e) => _ = _engine.ToggleNightModeAsync();

    private void BtnVoiceMode_Click(object sender, RoutedEventArgs e) => _ = _engine.ToggleVoiceModeAsync();

    private void BtnPower_Click(object sender, RoutedEventArgs e) => _ = _engine.TogglePowerAsync();

    private void BtnInputSource_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var shouldRestoreFocus = false;
        _inputMenuOpen = true;
        _deactivatedWhileInputMenuOpen = false;
        menu.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
                shouldRestoreFocus = true;
        };
        menu.Closed += (_, _) =>
        {
            _inputMenuOpen = false;

            if (!IsVisible || _presentation.State != FlyoutPresentationState.Open)
                return;

            if (shouldRestoreFocus || !_deactivatedWhileInputMenuOpen)
            {
                _deactivatedWhileInputMenuOpen = false;
                Activate();
                return;
            }

            _deactivatedWhileInputMenuOpen = false;
            CaptureDeactivationContext();
            RequestClose(causedByDeactivation: true);
        };

        foreach (var input in new[] { "hdmi", "tv", "bluetooth" })
        {
            var targetInput = input;
            var item = new MenuItem
            {
                Header = input.ToUpperInvariant(),
                IsCheckable = true,
                IsChecked = string.Equals(_engine.CurrentState.Function, input, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) =>
            {
                shouldRestoreFocus = true;
                _ = _engine.SetFunctionAsync(targetInput);
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = BtnInputSource;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BtnAuthenticate_Click(object sender, RoutedEventArgs e)
    {
        _authenticateAfterClose = true;
        RequestClose(causedByDeactivation: false);
    }
}
