using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;
using BraviaTheatre.UI.Views;

namespace BraviaTheatre.Tests;

public class ReleasePolishTests
{
    [Fact]
    public void FlyoutCompiledXamlLoadsWithReleaseControls()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var app = new BraviaTheatre.UI.App();
                app.InitializeComponent();
                using var engine = new BraviaEngine(new SonyCredentials(), "test-host");
                var flyout = new FlyoutWindow(engine, new AppSettings(), static () => { });
                var rearSlider = Assert.IsType<Slider>(flyout.FindName("SliderRear"));
                var soundFieldWaves = Assert.IsType<System.Windows.Shapes.Path>(flyout.FindName("IconSoundFieldWaves"));
                var soundFieldNote = Assert.IsType<System.Windows.Shapes.Path>(flyout.FindName("IconSoundFieldNote"));
                var soundFieldNoteHead = Assert.IsType<System.Windows.Shapes.Path>(flyout.FindName("IconSoundFieldNoteHead"));
                var voiceBubble = Assert.IsType<System.Windows.Shapes.Path>(flyout.FindName("IconVoiceBubble"));
                var voiceNote = Assert.IsType<System.Windows.Shapes.Path>(flyout.FindName("IconVoiceNote"));
                var voiceNoteHead = Assert.IsType<System.Windows.Shapes.Path>(flyout.FindName("IconVoiceNoteHead"));

                Assert.Equal(BraviaControlRanges.MinimumRearLevel, rearSlider.Minimum);
                Assert.Equal(BraviaControlRanges.MaximumRearLevel, rearSlider.Maximum);
                Assert.Equal(BraviaControlRanges.RearLevelStep, rearSlider.SmallChange);
                Assert.True(soundFieldWaves.Data.Bounds.Bottom < soundFieldNote.Data.Bounds.Bottom);
                Assert.True(soundFieldWaves.Data.Bounds.Bottom < soundFieldNoteHead.Data.Bounds.Bottom);
                Assert.True(voiceBubble.Data.Bounds.Contains(voiceNote.Data.Bounds));
                Assert.True(voiceBubble.Data.Bounds.Contains(voiceNoteHead.Data.Bounds));

                flyout.CloseForShutdown();
                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            completed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "Flyout XAML load timed out.");
        thread.Join();
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task RearLevelUsesTheDeviceAdvertisedSignedRange()
    {
        Assert.Equal(-10, BraviaControlRanges.MinimumRearLevel);
        Assert.Equal(10, BraviaControlRanges.MaximumRearLevel);
        Assert.Equal(1, BraviaControlRanges.RearLevelStep);

        var client = new EngineRegressionTests.FakeBraviaClient();
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new BraviaEngine(
            new SonyCredentials(),
            "test-host",
            55051,
            (_, _, _) => client,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        engine.StateChanged += state =>
        {
            if (state.Connected && state.Power) connected.TrySetResult(true);
        };

        try
        {
            engine.Start();
            await connected.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.True(await engine.SetRearLevelAsync(-100));
            Assert.Equal(BraviaControlRanges.MinimumRearLevel, engine.CurrentState.RearLevel);

            Assert.True(await engine.SetRearLevelAsync(100));
            Assert.Equal(BraviaControlRanges.MaximumRearLevel, engine.CurrentState.RearLevel);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public void DailyLogsUseProductNameDateAndFourteenDayRetention()
    {
        var appDataDirectory = NewTemporaryDirectory();
        try
        {
            var now = new DateTime(2026, 8, 25, 17, 30, 0, DateTimeKind.Local);
            var currentPath = DailyLogFile.GetPath(appDataDirectory, now);

            Assert.Equal(
                Path.Combine(appDataDirectory, "Logs", "BraviaTheatrePC-2026-08-25.log"),
                currentPath);

            DailyLogFile.AppendLine(appDataDirectory, now, "current");
            DailyLogFile.AppendLine(appDataDirectory, now.AddDays(-13), "retained");
            DailyLogFile.AppendLine(appDataDirectory, now.AddDays(-14), "expired");
            DailyLogFile.DeleteExpiredFiles(appDataDirectory, now);

            Assert.Equal("current" + Environment.NewLine, File.ReadAllText(currentPath));
            Assert.True(File.Exists(DailyLogFile.GetPath(appDataDirectory, now.AddDays(-13))));
            Assert.False(File.Exists(DailyLogFile.GetPath(appDataDirectory, now.AddDays(-14))));
        }
        finally
        {
            Directory.Delete(appDataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WebViewAuthenticationProfileIsSessionScopedAndRemovedAfterUse()
    {
        var appDataDirectory = NewTemporaryDirectory();
        try
        {
            var staleRoot = WebViewProfileService.GetRootDirectory(appDataDirectory);
            Directory.CreateDirectory(staleRoot);
            File.WriteAllText(Path.Combine(staleRoot, "stale-cache"), "old");

            Assert.True(WebViewProfileService.DeleteStaleProfiles(appDataDirectory));
            Assert.False(Directory.Exists(staleRoot));

            Directory.CreateDirectory(staleRoot);
            File.WriteAllText(Path.Combine(staleRoot, "stale-cache"), "old");
            var sessionDirectory = WebViewProfileService.PrepareSessionDirectory(appDataDirectory);
            Assert.StartsWith(staleRoot, sessionDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(staleRoot, "stale-cache")));

            File.WriteAllText(Path.Combine(sessionDirectory, "cookie-cache"), "temporary");
            await WebViewProfileService.CleanupSessionAsync(
                sessionDirectory,
                browserProcessId: null,
                TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(sessionDirectory));
            Assert.False(Directory.Exists(staleRoot));
        }
        finally
        {
            if (Directory.Exists(appDataDirectory))
                Directory.Delete(appDataDirectory, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"BraviaTheatrePC.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
