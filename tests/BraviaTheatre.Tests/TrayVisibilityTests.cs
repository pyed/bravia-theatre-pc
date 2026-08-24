using System.Diagnostics;
using System.Text.Json;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;
using Xunit;

namespace BraviaTheatre.Tests;

public class TrayVisibilityTests
{
    [Fact]
    public void GuidanceStartsPendingAndCopyPreservesCompletion()
    {
        var settings = new AppSettings();
        Assert.False(settings.TrayIconGuidanceShown);

        settings.TrayIconGuidanceShown = true;
        Assert.True(settings.Copy().TrayIconGuidanceShown);
    }

    [Fact]
    public void GuidanceCompletionRoundTripsThroughSettingsJson()
    {
        var json = JsonSerializer.Serialize(new AppSettings { TrayIconGuidanceShown = true });
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.True(restored.TrayIconGuidanceShown);
    }

    [Fact]
    public void TaskbarSettingsLauncherUsesTheSupportedWindowsUri()
    {
        ProcessStartInfo? observed = null;

        var result = TaskbarSettingsService.TryOpenTaskbarSettings(
            startInfo =>
            {
                observed = startInfo;
                return null;
            },
            out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.NotNull(observed);
        Assert.Equal(TaskbarSettingsService.TaskbarSettingsUri, observed.FileName);
        Assert.True(observed.UseShellExecute);
    }

    [Fact]
    public void TaskbarSettingsLauncherReportsShellFailures()
    {
        var result = TaskbarSettingsService.TryOpenTaskbarSettings(
            _ => throw new InvalidOperationException("shell unavailable"),
            out var error);

        Assert.False(result);
        Assert.Contains("shell unavailable", error, StringComparison.Ordinal);
    }
}
