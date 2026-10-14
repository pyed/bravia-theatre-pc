using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

[assembly: AssemblyFixture(typeof(BraviaTheatre.Tests.WpfApplicationFixture))]

namespace BraviaTheatre.Tests;

/// <summary>
/// The test run's one WPF application, on its own STA thread. WPF allows a single
/// <c>Application</c> per process, so every UI check used to live in one test that owned it.
/// Tests take this fixture and run their bodies on its thread with <see cref="Run"/>.
/// </summary>
/// <remarks>
/// This is a plain <c>Application</c> with the app's styles, not the real App: a running
/// dispatcher raises Startup even without <c>Application.Run</c>, and the App's startup takes the
/// single-instance mutex, reads the user's settings and credentials, and opens sign-in when there
/// are none. With a copy of the app running it signals that copy to show its flyout instead.
/// </remarks>
public sealed class WpfApplicationFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    public WpfApplicationFixture()
    {
        Dispatcher? dispatcher = null;
        ExceptionDispatchInfo? failure = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/BraviaTheatrePC;component/AppResources.xaml", UriKind.Absolute)
                    }
                };
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                ready.Set();
            }

            if (failure is null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test UI thread"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        failure?.Throw();
        _dispatcher = dispatcher!;
    }

    /// <summary>Runs a test body on the UI thread and rethrows anything it throws.</summary>
    public void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        _dispatcher.Invoke(() =>
        {
            try
            {
                // A shut-down application fails every window with a confusing error.
                Assert.NotNull(Application.Current);
                body();
            }
            catch (Exception error)
            {
                // Caught here so a failing test cannot stop the UI thread for the others.
                failure = ExceptionDispatchInfo.Capture(error);
            }
        });
        failure?.Throw();
    }

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}
