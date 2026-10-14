using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;

namespace BraviaTheatre.Tests;

/// <summary>
/// Snapshots and live notifications read state through one table of fields. They used to map
/// paths separately, and had drifted: only notifications knew playback_control.power.
/// </summary>
public class StateFieldMappingTests
{
    [Fact]
    public void EveryQueriedPathUpdatesAField()
    {
        Assert.All(BraviaEngine.QueriedPaths, path => Assert.Contains(path, BraviaEngine.MappedPaths));
    }

    [Theory]
    [InlineData("power", true)]
    [InlineData("playback_control.power", "on")]
    [InlineData("volume", 42)]
    [InlineData("volume", "not a number")]
    [InlineData("mute", "true")]
    [InlineData("sound_setting.night_mode", 1)]
    [InlineData("sound_setting.sound_field", "active")]
    [InlineData("sound_setting.voice_mode", true)]
    [InlineData("sound_setting.volume.bass", " MAX ")]
    [InlineData("sound_setting.volume.bass", "loud")]
    [InlineData("sound_setting.volume.rear", -3)]
    [InlineData("playback_control.function", "tv")]
    [InlineData("playback_control.function", "none")]
    [InlineData("playback_control.audio_format", "dolby_atmos")]
    [InlineData("audio_output.stream_info.audio_format", "NoAudio")]
    [InlineData("audio_output.stream_info.channel_info", "5.1.2")]
    [InlineData("unrelated.path", 7)]
    public void SnapshotAndNotificationReadAValueTheSameWay(string path, object value)
    {
        using var fromSnapshot = ConnectedEngine();
        using var fromNotification = ConnectedEngine();

        fromSnapshot.ApplySnapshot(new Dictionary<string, object?> { [path] = value }, "Bar");
        fromNotification.ApplyDelta(path, value);

        Assert.Equal(fromSnapshot.CurrentState, fromNotification.CurrentState);
    }

    [Fact]
    public void BothReadTheAlternativePowerPath()
    {
        using var fromSnapshot = ConnectedEngine();
        using var fromNotification = ConnectedEngine();

        fromSnapshot.ApplySnapshot(new Dictionary<string, object?> { ["playback_control.power"] = "on" }, "Bar");
        fromNotification.ApplyDelta("playback_control.power", "on");

        Assert.True(fromSnapshot.CurrentState.Power);
        Assert.True(fromNotification.CurrentState.Power);
    }

    [Fact]
    public void UnusableValuesLeaveTheFieldAlone()
    {
        using var engine = ConnectedEngine();

        engine.ApplySnapshot(new Dictionary<string, object?> { ["volume"] = 30, ["sound_setting.volume.bass"] = "max" }, "Bar");
        engine.ApplySnapshot(new Dictionary<string, object?> { ["volume"] = "loud", ["sound_setting.volume.bass"] = "boom" }, "Bar");
        engine.ApplyDelta("volume", "louder");

        Assert.Equal(30, engine.CurrentState.Volume);
        Assert.Equal("max", engine.CurrentState.Bass);
    }

    // Both start from the same connected state, so only the value under test differs.
    private static BraviaEngine ConnectedEngine()
    {
        var engine = new BraviaEngine(new SonyCredentials());
        engine.ApplySnapshot(new Dictionary<string, object?>
        {
            ["power"] = false,
            ["volume"] = 10,
            ["playback_control.audio_format"] = "lpcm",
            ["playback_control.audio_channel"] = "2.0"
        }, "Bar");
        return engine;
    }
}
