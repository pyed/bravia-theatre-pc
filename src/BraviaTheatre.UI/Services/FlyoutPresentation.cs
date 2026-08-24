using System;

namespace BraviaTheatre.UI.Services;

internal enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom
}

internal readonly record struct PixelPoint(int X, int Y);

internal enum TrayActivationKind
{
    Mouse,
    Keyboard
}

internal readonly record struct TrayActivation(
    TrayActivationKind Kind,
    PixelPoint? ScreenPoint,
    uint MessageTime);

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public int CenterX => Left + (Width / 2);
    public int CenterY => Top + (Height / 2);

    public static PixelRect FromPositionAndSize(int left, int top, int width, int height) =>
        new(left, top, left + Math.Max(0, width), top + Math.Max(0, height));

    public bool Contains(PixelPoint point, int tolerance = 0) =>
        point.X >= Left - tolerance && point.X <= Right + tolerance &&
        point.Y >= Top - tolerance && point.Y <= Bottom + tolerance;
}

internal readonly record struct FlyoutPlacement(PixelRect Bounds, TaskbarEdge TaskbarEdge);

internal static class FlyoutPlacementCalculator
{
    public static FlyoutPlacement Calculate(
        PixelRect monitor,
        PixelRect workArea,
        PixelRect anchor,
        int flyoutWidth,
        int flyoutHeight,
        int margin)
    {
        flyoutWidth = Math.Max(1, flyoutWidth);
        flyoutHeight = Math.Max(1, flyoutHeight);
        margin = Math.Max(0, margin);
        flyoutWidth = Math.Min(flyoutWidth, Math.Max(1, workArea.Width - (2 * margin)));
        flyoutHeight = Math.Min(flyoutHeight, Math.Max(1, workArea.Height - (2 * margin)));

        var edge = DetectTaskbarEdge(monitor, workArea, anchor);
        int left;
        int top;

        if (edge is TaskbarEdge.Top or TaskbarEdge.Bottom)
        {
            left = ClampCentered(anchor.CenterX, flyoutWidth, workArea.Left, workArea.Right, margin);
            top = edge == TaskbarEdge.Top
                ? workArea.Top + margin
                : workArea.Bottom - flyoutHeight - margin;
        }
        else
        {
            left = edge == TaskbarEdge.Left
                ? workArea.Left + margin
                : workArea.Right - flyoutWidth - margin;
            top = ClampCentered(anchor.CenterY, flyoutHeight, workArea.Top, workArea.Bottom, margin);
        }

        var maxLeft = Math.Max(workArea.Left, workArea.Right - flyoutWidth);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - flyoutHeight);
        left = Math.Clamp(left, workArea.Left, maxLeft);
        top = Math.Clamp(top, workArea.Top, maxTop);

        return new FlyoutPlacement(
            PixelRect.FromPositionAndSize(left, top, flyoutWidth, flyoutHeight),
            edge);
    }

    internal static TaskbarEdge DetectTaskbarEdge(PixelRect monitor, PixelRect workArea, PixelRect anchor)
    {
        var leftInset = Math.Max(0, workArea.Left - monitor.Left);
        var topInset = Math.Max(0, workArea.Top - monitor.Top);
        var rightInset = Math.Max(0, monitor.Right - workArea.Right);
        var bottomInset = Math.Max(0, monitor.Bottom - workArea.Bottom);
        var largestInset = Math.Max(Math.Max(leftInset, topInset), Math.Max(rightInset, bottomInset));

        if (largestInset > 0)
        {
            // Prefer the conventional bottom edge when equal insets are reported.
            if (bottomInset == largestInset) return TaskbarEdge.Bottom;
            if (topInset == largestInset) return TaskbarEdge.Top;
            if (leftInset == largestInset) return TaskbarEdge.Left;
            return TaskbarEdge.Right;
        }

        // Auto-hidden taskbars do not reduce the work area. The tray icon's nearest
        // monitor edge still tells us which direction the flyout should use.
        var distances = new (TaskbarEdge Edge, int Distance)[]
        {
            (TaskbarEdge.Bottom, Math.Abs(monitor.Bottom - anchor.CenterY)),
            (TaskbarEdge.Top, Math.Abs(anchor.CenterY - monitor.Top)),
            (TaskbarEdge.Left, Math.Abs(anchor.CenterX - monitor.Left)),
            (TaskbarEdge.Right, Math.Abs(monitor.Right - anchor.CenterX))
        };

        var nearest = distances[0];
        foreach (var candidate in distances)
        {
            if (candidate.Distance < nearest.Distance) nearest = candidate;
        }
        return nearest.Edge;
    }

    private static int ClampCentered(int anchorCenter, int popupSize, int start, int end, int margin)
    {
        var minimum = start + margin;
        var maximum = end - popupSize - margin;
        if (maximum < minimum) return start;
        return Math.Clamp(anchorCenter - (popupSize / 2), minimum, maximum);
    }
}

internal static class TrayInteractionCorrelator
{
    internal static readonly TimeSpan MaximumInteractionDuration = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumKeyboardInteractionDuration = TimeSpan.FromSeconds(2);

    public static bool IsSameInteraction(
        TrayActivation activation,
        uint deactivationMessageTime,
        PixelPoint? deactivationPoint,
        PixelRect? trayBounds,
        bool deactivationClosePending)
    {
        if (!deactivationClosePending ||
            Elapsed(deactivationMessageTime, activation.MessageTime) > MaximumInteractionDuration)
        {
            return false;
        }

        if (activation.Kind == TrayActivationKind.Keyboard)
            return Elapsed(deactivationMessageTime, activation.MessageTime) <= MaximumKeyboardInteractionDuration;

        if (deactivationPoint is not { } downPoint)
            return activation.ScreenPoint == null &&
                   Elapsed(deactivationMessageTime, activation.MessageTime) <= TimeSpan.FromSeconds(2);

        if (trayBounds is { } bounds &&
            bounds.Contains(downPoint, tolerance: 14) &&
            (activation.ScreenPoint is not { } upPoint || bounds.Contains(upPoint, tolerance: 14)))
        {
            return true;
        }

        if (activation.ScreenPoint is not { } callbackPoint)
            return false;

        return Math.Abs(callbackPoint.X - downPoint.X) <= 18 &&
               Math.Abs(callbackPoint.Y - downPoint.Y) <= 18;
    }

    internal static TimeSpan Elapsed(uint earlier, uint later) =>
        TimeSpan.FromMilliseconds(unchecked(later - earlier));
}

internal enum FlyoutPresentationState
{
    Hidden,
    Opening,
    Open,
    Closing
}

internal readonly record struct FlyoutTransition(long Generation, FlyoutPresentationState Target)
{
    public bool Exists => Generation != 0;
    public static FlyoutTransition None => default;
}

internal sealed class FlyoutTransitionController
{
    private long _generation;
    private bool _suppressCorrelatedTrayToggle;

    public FlyoutPresentationState State { get; private set; } = FlyoutPresentationState.Hidden;
    public bool IsAwaitingCorrelatedTrayToggle => _suppressCorrelatedTrayToggle;

    public FlyoutTransition Show()
    {
        if (State is FlyoutPresentationState.Open or FlyoutPresentationState.Opening)
            return FlyoutTransition.None;

        _suppressCorrelatedTrayToggle = false;
        State = FlyoutPresentationState.Opening;
        return NewTransition(State);
    }

    public FlyoutTransition Hide(bool causedByDeactivation)
    {
        if (State is FlyoutPresentationState.Hidden or FlyoutPresentationState.Closing)
            return FlyoutTransition.None;

        if (causedByDeactivation)
        {
            _suppressCorrelatedTrayToggle = true;
        }
        else
        {
            _suppressCorrelatedTrayToggle = false;
        }

        State = FlyoutPresentationState.Closing;
        return NewTransition(State);
    }

    public FlyoutTransition ToggleFromTray(bool sameDeactivationInteraction)
    {
        if (_suppressCorrelatedTrayToggle && sameDeactivationInteraction)
        {
            _suppressCorrelatedTrayToggle = false;
            return FlyoutTransition.None;
        }

        _suppressCorrelatedTrayToggle = false;
        return State is FlyoutPresentationState.Open or FlyoutPresentationState.Opening
            ? Hide(causedByDeactivation: false)
            : Show();
    }

    public bool Complete(FlyoutTransition transition)
    {
        if (!transition.Exists || transition.Generation != _generation || transition.Target != State)
            return false;

        State = transition.Target switch
        {
            FlyoutPresentationState.Opening => FlyoutPresentationState.Open,
            FlyoutPresentationState.Closing => FlyoutPresentationState.Hidden,
            _ => State
        };
        return true;
    }

    public bool IsCurrent(FlyoutTransition transition) =>
        transition.Exists && transition.Generation == _generation && transition.Target == State;

    public void ResolveTrayInteraction() => _suppressCorrelatedTrayToggle = false;

    public void ForceHidden()
    {
        _generation++;
        _suppressCorrelatedTrayToggle = false;
        State = FlyoutPresentationState.Hidden;
    }

    private FlyoutTransition NewTransition(FlyoutPresentationState target) =>
        new(++_generation, target);
}
