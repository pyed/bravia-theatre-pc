using BraviaTheatre.UI.Models;

namespace BraviaTheatre.Tests;

/// <summary>
/// An unreadable settings file was replaced with defaults during the same start, losing the user's
/// shortcuts, static host and log level with no copy kept (HANDOFF P2-15).
/// </summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("BraviaTheatreSettingsTests").FullName;

    [Fact]
    public void AnUnreadableFileIsKeptAsideBeforeDefaultsAreUsed()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ \"HotkeyMute\": \"Ctrl + Shift + M\", truncated");

        var settings = AppSettings.Load(path, out var warning);

        Assert.Equal(new AppSettings().HotkeyMute, settings.HotkeyMute);
        Assert.False(File.Exists(path));
        Assert.Equal("{ \"HotkeyMute\": \"Ctrl + Shift + M\", truncated", File.ReadAllText(path + ".bad"));
        Assert.NotNull(warning);
        Assert.Contains(path + ".bad", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadableFileLoadsWithoutAWarning()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{ \"StaticHost\": \"192.168.1.50\", \"SoundbarNetwork\": \"192.168.1.0/24\" }");

        var settings = AppSettings.Load(path, out var warning);

        Assert.Null(warning);
        Assert.Equal("192.168.1.50", settings.StaticHost);
        Assert.Equal("192.168.1.0/24", settings.SoundbarNetwork);
        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public void AMissingFileMeansDefaults()
    {
        var settings = AppSettings.Load(Path.Combine(_directory, "settings.json"), out var warning);

        Assert.Null(warning);
        Assert.Null(settings.SoundbarNetwork);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
