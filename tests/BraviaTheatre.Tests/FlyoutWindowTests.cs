using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Views;

namespace BraviaTheatre.Tests;

/// <summary>
/// The compiled flyout as it ships. These checks were one STA test that owned the WPF
/// application; they now share <see cref="WpfApplicationFixture"/>.
/// </summary>
public class FlyoutWindowTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void RearSliderUsesTheDeviceRange() => WithFlyout(flyout =>
    {
        // Assigned in code: x:Static on a const compiles but fails in the single-file publish.
        var rearSlider = Find<Slider>(flyout, "SliderRear");

        Assert.Equal(BraviaControlRanges.MinimumRearLevel, rearSlider.Minimum);
        Assert.Equal(BraviaControlRanges.MaximumRearLevel, rearSlider.Maximum);
        Assert.Equal(BraviaControlRanges.RearLevelStep, rearSlider.SmallChange);
    });

    [Fact]
    public void SoundFieldAndVoiceIconsKeepTheirShapes() => WithFlyout(flyout =>
    {
        var waves = Find<System.Windows.Shapes.Path>(flyout, "IconSoundFieldWaves");
        var note = Find<System.Windows.Shapes.Path>(flyout, "IconSoundFieldNote");
        var noteHead = Find<System.Windows.Shapes.Path>(flyout, "IconSoundFieldNoteHead");
        var voiceBubble = Find<System.Windows.Shapes.Path>(flyout, "IconVoiceBubble");
        var voiceNote = Find<System.Windows.Shapes.Path>(flyout, "IconVoiceNote");
        var voiceNoteHead = Find<System.Windows.Shapes.Path>(flyout, "IconVoiceNoteHead");

        // The waves sit above the note, centred on it, without touching it.
        Assert.True(waves.Data.Bounds.Bottom < note.Data.Bounds.Bottom);
        Assert.True(waves.Data.Bounds.Bottom < noteHead.Data.Bounds.Bottom);
        var waveCenter = waves.Data.Bounds.Left + (waves.Data.Bounds.Width / 2);
        var noteLeft = Math.Min(note.Data.Bounds.Left, noteHead.Data.Bounds.Left);
        var noteRight = Math.Max(note.Data.Bounds.Right, noteHead.Data.Bounds.Right);
        Assert.Equal(waveCenter, noteLeft + ((noteRight - noteLeft) / 2), precision: 3);
        var paintedWaveBottom = waves.Data.Bounds.Bottom + (waves.StrokeThickness / 2);
        var paintedNoteTop = note.Data.Bounds.Top - (note.StrokeThickness / 2);
        Assert.True(paintedNoteTop - paintedWaveBottom >= 0.5);

        Assert.True(voiceBubble.Data.Bounds.Contains(voiceNote.Data.Bounds));
        Assert.True(voiceBubble.Data.Bounds.Contains(voiceNoteHead.Data.Bounds));
    });

    [Theory]
    [InlineData("min", "MIN", false, true)]
    [InlineData("mid", "MID", true, true)]
    [InlineData("max", "MAX", true, false)]
    public void BassStepperShowsTheLevelAndOnlyTheStepsLeft(string bass, string shown, bool canDecrease, bool canIncrease) =>
        WithFlyout(flyout =>
        {
            flyout.UpdateState(new SoundbarState { Connected = true, Power = true, Bass = bass });

            Assert.Equal(shown, Find<TextBlock>(flyout, "TxtBassLevel").Text);
            Assert.Equal(canDecrease, Find<Button>(flyout, "BtnBassDecrease").IsEnabled);
            Assert.Equal(canIncrease, Find<Button>(flyout, "BtnBassIncrease").IsEnabled);
        });

    [Theory]
    [InlineData("min", -1, "min")]
    [InlineData("min", 1, "mid")]
    [InlineData("mid", 1, "max")]
    [InlineData("max", 1, "max")]
    public void BassStepsStayWithinTheLevels(string from, int direction, string expected)
    {
        Assert.Equal(expected, FlyoutWindow.StepBassLevel(from, direction));
    }

    [Fact]
    public void ExactlyOneStatusCardIsVisible() => WithFlyout(flyout =>
    {
        // The codec, sign-in, and soundbar-selection cards share one grid row.
        var codecCard = Find<Border>(flyout, "CodecCard");
        var authCard = Find<Border>(flyout, "AuthRequiredCard");
        var associationCard = Find<Border>(flyout, "DeviceAssociationCard");
        Border[] cards = [codecCard, authCard, associationCard];

        flyout.UpdateState(SoundbarState.Disconnected with { DeviceAssociationRequired = true });
        Assert.Equal(Visibility.Visible, associationCard.Visibility);
        Assert.Single(cards, card => card.Visibility == Visibility.Visible);

        flyout.UpdateState(SoundbarState.Disconnected with { AuthRequired = true, DeviceAssociationRequired = true });
        Assert.Equal(Visibility.Visible, authCard.Visibility);
        Assert.Single(cards, card => card.Visibility == Visibility.Visible);

        flyout.UpdateState(new SoundbarState { Connected = true, Power = true });
        Assert.Equal(Visibility.Visible, codecCard.Visibility);
        Assert.Single(cards, card => card.Visibility == Visibility.Visible);
    });

    [Fact]
    public void VolumeSliderFollowsTheSoundbarAfterAnInterruptedDrag() => WithFlyout(flyout =>
    {
        // A drag the flyout loses mid-way never delivers its mouse-up.
        var volumeSlider = Find<Slider>(flyout, "SliderVolume");
        flyout.UpdateState(new SoundbarState { Connected = true, Power = true, Volume = 10 });
        volumeSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent
        });
        volumeSlider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = UIElement.LostMouseCaptureEvent
        });

        flyout.UpdateState(new SoundbarState { Connected = true, Power = true, Volume = 42 });

        Assert.Equal(42, volumeSlider.Value);
    });

    private void WithFlyout(Action<FlyoutWindow> test) => wpf.Run(() =>
    {
        using var engine = new BraviaEngine(new SonyCredentials(), "test-host");
        var flyout = new FlyoutWindow(engine, new AppSettings(), static () => { }, static () => { });
        try
        {
            test(flyout);
        }
        finally
        {
            flyout.CloseForShutdown();
        }
    });

    private static T Find<T>(FrameworkElement root, string name) => Assert.IsType<T>(root.FindName(name));
}
