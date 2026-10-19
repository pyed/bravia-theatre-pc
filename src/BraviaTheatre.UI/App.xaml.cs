using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BraviaTheatre.Core;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.Core.Tv;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;
using BraviaTheatre.UI.Views;

namespace BraviaTheatre.UI;

public partial class App : Application
{
    private const string MutexName = "BraviaTheatrePC_SingleInstance_Mutex_2026";
    private const int MaxLoggedStackFrames = 16;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    private BraviaEngine? _engine;
    private Action<SoundbarState>? _engineStateChangedHandler;
    private FlyoutWindow? _flyout;
    private TrayMenuWindow? _trayMenu;
    private NativeTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private SonyCredentialLifecycle? _credentialLifecycle;
    private AppSettings _settings = new();
    private SettingsWindow? _settingsWindow;
    private AuthDialog? _authDialog;
    private DeviceSelectionDialog? _deviceSelectionDialog;
    private CancellationTokenSource? _deviceRecoveryCts;
    private CancellationTokenSource? _tvFollowCts;
    private string _tvFollowHost = "";
    private bool _soundbarPickerDismissed;
    private bool _isShuttingDown;
    private bool _startupCompleted;
    private readonly ErrorDialogGate _errorDialogs = new();

    private readonly EngineStateRelay _stateRelay;
    private SoundbarState _latestState = SoundbarState.Disconnected;
    private readonly TrayContextMenuCoordinator _trayMenuCoordinator = new();
    private readonly TrayAttentionNotifier _attentionNotifier = new();

    public App()
    {
        _stateRelay = new EngineStateRelay(
            action =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return false;
                Dispatcher.BeginInvoke(action);
                return true;
            },
            state =>
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    ApplyStateToUi(state);
            });
    }

    public static string GetAppDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraviaTheatrePC");
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }

    private static DiagnosticLevel _currentLogLevel = DiagnosticLevel.Critical;

    public static void SetLogLevel(string? levelStr)
    {
        _currentLogLevel = levelStr?.ToLowerInvariant() switch
        {
            "verbose" => DiagnosticLevel.Verbose,
            "info" => DiagnosticLevel.Info,
            _ => DiagnosticLevel.Critical
        };
    }

    private static readonly object _logLock = new();
    private static DateTime _lastLogMaintenanceDate = DateTime.MinValue;

    public static void Log(string message, DiagnosticLevel level)
    {
        if (level > _currentLogLevel) return;

        lock (_logLock)
        {
            try
            {
                var now = DateTime.Now;
                var appDataDirectory = GetAppDataDir();
                if (_lastLogMaintenanceDate != now.Date)
                {
                    DailyLogFile.DeleteExpiredFiles(appDataDirectory, now);
                    _lastLogMaintenanceDate = now.Date;
                }

                var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                DailyLogFile.AppendLine(appDataDirectory, now, line);
            }
            catch
            {
                // Ignore logging errors
            }
        }
    }

    // Credential renewal diagnostics are rare and explain why sign-in was or was not requested,
    // so every log level keeps them.
    private static void LogCredentialDiagnostic(string message) => Log(message, DiagnosticLevel.Critical);

    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Prevent application from closing when modal dialogs close
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Hardware rendering loads the graphics driver's user-mode stack into the process on
        // the first window and keeps it for good: with an NVIDIA card that was over 100 MB of
        // private memory for a tray app that draws two small panels. The CPU draws them as
        // well, and DWM still supplies the acrylic behind them.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var failure = args.ExceptionObject is Exception exception
                ? FormatExceptionForLog(exception)
                : args.ExceptionObject?.GetType().Name ?? "UnknownFailure";
            Log($"FATAL UNHANDLED EXCEPTION: {failure}", DiagnosticLevel.Critical);
            MessageBox.Show("A fatal application error occurred. Restart BRAVIA Theatre PC and try again.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            if (args.Exception is OperationCanceledException or ObjectDisposedException)
            {
                args.Handled = true;
                return;
            }
            Log($"DISPATCHER UNHANDLED EXCEPTION: {FormatExceptionForLog(args.Exception)}", DiagnosticLevel.Critical);
            if (_errorDialogs.TryOpen(args.Exception))
            {
                try
                {
                    // Background messages fail too, so do not assume the user just did something.
                    MessageBox.Show("BRAVIA Theatre PC ran into an unexpected problem. If it keeps happening, restart the app. Details are in the log, which Settings can open.",
                        "BRAVIA Theatre PC", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    _errorDialogs.Close();
                }
            }
            args.Handled = true;
            if (!_startupCompleted)
                Dispatcher.BeginInvoke(ShutdownApp);
        };

        Log("Application starting...", DiagnosticLevel.Info);

        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            Log("Another instance is already running. Signaling Quick Settings flyout.", DiagnosticLevel.Info);
            uint wmShowFlyout = NativeMethods.RegisterWindowMessage("BRAVIA_THEATRE_PC_SHOW_FLYOUT");
            NativeMethods.PostMessage(HWND_BROADCAST, wmShowFlyout, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        // Before any window exists: WPF reads the stylus switch when it creates the first one.
        PinNativeLibraries();
        _ = Task.Run(DeleteStaleBundleExtractions);
        WebViewProfileService.DeleteStaleProfiles(GetAppDataDir());
        try
        {
            var now = DateTime.Now;
            lock (_logLock)
            {
                DailyLogFile.DeleteExpiredFiles(GetAppDataDir(), now);
                _lastLogMaintenanceDate = now.Date;
            }
        }
        catch
        {
            // Logging must never prevent application startup.
        }

        _settings = AppSettings.Load(out var settingsWarning);
        SetLogLevel(_settings.LogLevel);
        if (!string.IsNullOrWhiteSpace(settingsWarning))
            MessageBox.Show(settingsWarning, "Settings Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

        var credentialStore = new SonyCredentialStore(Path.Combine(GetAppDataDir(), "credentials.dat"));
        var credentialResult = credentialStore.Load();
        if (!string.IsNullOrWhiteSpace(credentialResult.Message))
            MessageBox.Show(credentialResult.Message, "Credential Storage", MessageBoxButton.OK, MessageBoxImage.Warning);

        _credentialLifecycle = new SonyCredentialLifecycle(
            credentialResult.Credentials,
            static (credentials, checkpointRotatedRefreshTokenAsync, cancellationToken, reassociationSelector) =>
                SonyOAuth.RefreshSessionKeysAsync(
                    credentials,
                    cancellationToken,
                    checkpointRotatedRefreshTokenAsync: checkpointRotatedRefreshTokenAsync,
                    diagnosticLog: LogCredentialDiagnostic,
                    reassociationSelector: reassociationSelector),
            (credentials, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!credentialStore.TrySave(credentials, out var error))
                    throw new InvalidOperationException(error ?? "Could not save protected Sony credentials.");
                return Task.CompletedTask;
            })
        { DiagnosticLog = LogCredentialDiagnostic };

        if (_credentialLifecycle.CurrentCredentials?.IsValid != true)
        {
            Log("Valid protected credentials were not found. Opening AuthDialog...", DiagnosticLevel.Info);
            if (!ShowAuthDialog(restartEngine: false))
            {
                Log("AuthDialog cancelled. Shutting down.", DiagnosticLevel.Info);
                Shutdown();
                return;
            }
        }

        if (_credentialLifecycle.CurrentCredentials?.IsValid != true)
        {
            Log("No valid credentials after AuthDialog. Shutting down.", DiagnosticLevel.Info);
            MessageBox.Show("Valid Sony credentials are required to run.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        Log("Credentials loaded successfully.", DiagnosticLevel.Info);

        // Initialize System Tray
        var trayIconShown = InitializeTray();
        ReplaceEngine();
        ApplyTvFollow();
        _startupCompleted = true;
        if (trayIconShown)
            Dispatcher.BeginInvoke(new Action(ShowTrayIconGuidanceOnce));
    }

    private static void PinNativeLibraries()
    {
        var missing = BundledNativeLibraries.Pin();
        if (missing.Count == 0) return;

        Log($"Native libraries could not be loaded: {string.Join(", ", missing)}.", DiagnosticLevel.Critical);
        if (missing.Contains(BundledNativeLibraries.PenImc))
        {
            // WPF's pen and touch stack cannot start without it, and would throw on every
            // device change once a digitizer appears. Touch still arrives as mouse input.
            AppContext.SetSwitch(BundledNativeLibraries.DisableStylusAndTouchSupportSwitch, true);
        }
    }

    private static void DeleteStaleBundleExtractions()
    {
        try
        {
            var deleted = BundleExtractionCleanup.DeleteStaleFolders();
            if (deleted > 0)
                Log($"Removed {deleted} native-library folder(s) that other builds left in the temp directory.", DiagnosticLevel.Info);
        }
        catch (Exception error)
        {
            Log($"Could not remove old native-library folders: {error.GetType().Name}.", DiagnosticLevel.Info);
        }
    }

    internal static string FormatExceptionForLog(Exception exception)
    {
        var details = new List<string>();
        var innermost = exception;
        for (Exception? current = exception; current != null && details.Count < 8; current = current.InnerException)
        {
            var message = current.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            details.Add(string.IsNullOrEmpty(message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {message}");
            innermost = current;
        }

        // Runtime messages can be generic ("Dll was not found." names no library), so the
        // frames are what show where a failure came from.
        var frames = (innermost.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(MaxLoggedStackFrames);
        var stack = string.Join(" | ", frames);

        var summary = string.Join(" -> ", details);
        return stack.Length == 0 ? summary : $"{summary} | {stack}";
    }

    private bool InitializeTray()
    {
        Log("Initializing Native System Tray Icon...", DiagnosticLevel.Verbose);
        _trayIcon = new NativeTrayIcon
        {
            LogAction = message => Log(message, DiagnosticLevel.Critical),
            ToggleAction = activation =>
            {
                Log("Tray activated. Toggling flyout...", DiagnosticLevel.Verbose);
                TogglePrimarySurface(activation);
            },
            ShowAction = () =>
            {
                Log("Application activation requested. Showing flyout...", DiagnosticLevel.Verbose);
                ShowPrimarySurface();
            },
            ContextMenuAction = ShowTrayContextMenu,
            IconRecreatedAction = () => _flyout?.RepositionIfVisible(TryGetTrayAnchor()),
            NotificationClickedAction = () =>
            {
                Log("Tray notification selected. Showing flyout...", DiagnosticLevel.Verbose);
                ShowPrimarySurface();
            }
        };
        var shown = _trayIcon.Show(IconHelper.GetTrayIcon("idle"), "BRAVIA Theatre PC");
        if (shown)
            Log("Native System Tray Icon shown.", DiagnosticLevel.Info);
        else
            Log("Native System Tray Icon failed to initialize.", DiagnosticLevel.Critical);
        return shown;
    }

    private void ShowTrayIconGuidanceOnce()
    {
        if (_isShuttingDown || _settings.TrayIconGuidanceShown) return;

        _settings.TrayIconGuidanceShown = true;
        if (!_settings.TrySave(out var saveError))
            Log(saveError ?? "Could not save the tray icon guidance preference.", DiagnosticLevel.Info);

        var result = MessageBox.Show(
            "Windows controls which app icons remain visible next to the clock. To keep BRAVIA Theatre PC visible, enable it under Other system tray icons, or drag it out of the hidden-icons menu.\n\nOpen Taskbar settings now?",
            "Keep BRAVIA Theatre PC Visible",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);

        if (result == MessageBoxResult.Yes &&
            !TaskbarSettingsService.TryOpenTaskbarSettings(out var error))
        {
            MessageBox.Show(error ?? "Windows Taskbar settings could not be opened.",
                "Could Not Open Taskbar Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        var previousSettings = _settings;
        var win = new SettingsWindow(_settings, () => ShowAuthDialog(restartEngine: true));
        _settingsWindow = win;
        win.Closed += (_, _) => _settingsWindow = null;

        win.SettingsSaved += (updated) =>
        {
            _settings = updated;
            SetLogLevel(_settings.LogLevel);
            var connectionChanged =
                !string.Equals(previousSettings.StaticHost?.Trim(), updated.StaticHost?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                previousSettings.StaticPort != updated.StaticPort;

            if (connectionChanged && _credentialLifecycle?.CurrentCredentials?.IsValid == true)
            {
                ReplaceEngine();
            }
            else
            {
                _flyout?.ApplySettings(_settings);
                ApplyHotkeySettings(showUserError: true);
            }

            ApplyTvFollow();
        };

        win.ShowDialog();
    }

    private bool ShowAuthDialog(bool restartEngine)
    {
        if (_credentialLifecycle == null) return false;
        CancelDeviceRecovery();
        if (_authDialog != null)
        {
            _authDialog.Activate();
            return false;
        }

        var dialog = new AuthDialog(_credentialLifecycle);
        _authDialog = dialog;
        try
        {
            if (dialog.ShowDialog() != true || _credentialLifecycle.CurrentCredentials?.IsValid != true)
                return false;

            if (restartEngine) ReplaceEngine();
            return true;
        }
        finally
        {
            _authDialog = null;
        }
    }

    private async Task ChooseSoundbarAsync()
    {
        if (_isShuttingDown || _credentialLifecycle?.CurrentCredentials is not { IsValid: true } credentials)
            return;
        if (_deviceRecoveryCts != null)
        {
            _deviceSelectionDialog?.Activate();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _deviceRecoveryCts = cancellation;
        _soundbarPickerDismissed = false;
        try
        {
            var result = await _credentialLifecycle.RefreshAsync(credentials, cancellation.Token, SelectReassociatedDeviceAsync);
            if (cancellation.IsCancellationRequested || _isShuttingDown) return;
            if (result.Status == CredentialRenewalStatus.Succeeded)
            {
                ReplaceEngine();
            }
            else if (!_soundbarPickerDismissed)
            {
                MessageBox.Show(
                    result.Status == CredentialRenewalStatus.DeviceAssociationRequired
                        ? "No soundbar could be selected. Check that your soundbar is linked in BRAVIA Connect, then choose it again."
                        : "The soundbar could not be reconnected. Please try again.",
                    "Reconnect soundbar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the picker, replacing the engine, and shutdown cancel selection.
        }
        catch (Exception error)
        {
            Log($"[Credential renewal] Device selection failed; ExceptionType={error.GetType().Name}.", DiagnosticLevel.Critical);
            if (!_isShuttingDown && !cancellation.IsCancellationRequested)
                MessageBox.Show("The soundbar could not be reconnected. Please try again.",
                    "Reconnect soundbar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_deviceRecoveryCts, cancellation)) _deviceRecoveryCts = null;
        }
    }

    private async Task<string?> SelectReassociatedDeviceAsync(
        System.Collections.Generic.IReadOnlyList<SonyDeviceInfo> devices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selection = Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new DeviceSelectionDialog(devices);
            _deviceSelectionDialog = dialog;
            try
            {
                if (dialog.ShowDialog() == true) return dialog.SelectedDeviceId;

                // Dismissing the picker is a deliberate answer, not a failure to report back.
                _soundbarPickerDismissed = true;
                return null;
            }
            finally
            {
                _deviceSelectionDialog = null;
            }
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);

        // A cancelled engine/shutdown must release the lifecycle gate even if the
        // UI thread is currently waiting for that engine to finish stopping.
        return await selection.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CancelDeviceRecovery()
    {
        _deviceRecoveryCts?.Cancel();
        _deviceSelectionDialog?.Close();
    }

    private void ReplaceEngine()
    {
        CancelDeviceRecovery();
        var credentialLifecycle = _credentialLifecycle
            ?? throw new InvalidOperationException("Sony credential lifecycle is not initialized.");
        if (credentialLifecycle.CurrentCredentials?.IsValid != true)
            throw new InvalidOperationException("Valid Sony credentials are required to start the engine.");

        var oldEngine = _engine;
        var generation = InvalidateEngineState(oldEngine);
        ApplyStateToUi(SoundbarState.Disconnected);
        try { _hotkeyService?.Dispose(); } catch (Exception ex) { Log($"Hotkey cleanup warning: {ex.Message}", DiagnosticLevel.Info); }
        try { _flyout?.CloseForShutdown(); } catch (Exception ex) { Log($"Flyout cleanup warning: {ex.Message}", DiagnosticLevel.Info); }
        _flyout = null;
        try
        {
            oldEngine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"Engine cleanup warning: {ex.Message}", DiagnosticLevel.Info);
        }

        string? host = null;
        if (!string.IsNullOrWhiteSpace(_settings.StaticHost))
        {
            if (ControlEndpoint.TryNormalizeHost(_settings.StaticHost, out var staticHost))
                host = staticHost;
            else
                Log("Configured static host is not a valid host name or IP address; using automatic discovery instead.", DiagnosticLevel.Critical);
        }
        var port = _settings.StaticPort is >= 1 and <= 65535 ? _settings.StaticPort : 55051;
        var newEngine = new BraviaEngine(credentialLifecycle, host, port) { LogAction = Log };
        _engine = newEngine;
        _hotkeyService = new GlobalHotkeyService(newEngine);
        ApplyHotkeySettings(showUserError: false);
        _engineStateChangedHandler = state => OnEngineStateChanged(generation, state);
        newEngine.StateChanged += _engineStateChangedHandler;
        newEngine.Start();
        Log("Engine started and running in system tray.", DiagnosticLevel.Info);
    }

    // TV follow outlives engine replacements: it reads whichever engine is current when the TV
    // turns on, so a reconnect or a new sign-in does not need to restart it.
    private void ApplyTvFollow()
    {
        var host = _settings.TvHost?.Trim() ?? "";
        if (!string.IsNullOrEmpty(host) && !ControlEndpoint.TryNormalizeHost(host, out host))
        {
            Log("TV follow: the saved TV host is not valid; TV follow is off.", DiagnosticLevel.Critical);
            host = "";
        }

        if (_tvFollowCts != null && string.Equals(host, _tvFollowHost, StringComparison.OrdinalIgnoreCase))
            return;

        StopTvFollow();
        if (host.Length == 0 || _isShuttingDown) return;

        var cts = new CancellationTokenSource();
        var probe = new SamsungTvProbe(host);
        var follower = new TvPowerFollower(
            probe.ReadAsync,
            () =>
            {
                var state = Volatile.Read(ref _engine)?.CurrentState;
                return state is { Connected: true } ? state.Power : null;
            },
            () => Volatile.Read(ref _engine)?.SetPowerAsync(true) ?? Task.FromResult(false),
            log: Log);

        _tvFollowCts = cts;
        _tvFollowHost = host;
        Log("TV follow started.", DiagnosticLevel.Info);
        _ = Task.Run(() => follower.RunAsync(cts.Token)).ContinueWith(task =>
        {
            probe.Dispose();
            if (task.Exception != null)
                Log($"TV follow stopped: {task.Exception.InnerException?.GetType().Name}", DiagnosticLevel.Critical);
        }, TaskScheduler.Default);
    }

    private void StopTvFollow()
    {
        var cts = _tvFollowCts;
        _tvFollowCts = null;
        _tvFollowHost = "";
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private long InvalidateEngineState(BraviaEngine? engine)
    {
        var generation = _stateRelay.StartGeneration();
        var handler = _engineStateChangedHandler;
        _engineStateChangedHandler = null;
        if (engine != null && handler != null) engine.StateChanged -= handler;
        return generation;
    }

    private void ApplyHotkeySettings(bool showUserError)
    {
        if (_hotkeyService == null) return;
        var result = _settings.EnableGlobalHotkeys
            ? _hotkeyService.Register(_settings)
            : new HotkeyOperationResult(true, "Global hotkeys are disabled.");
        if (!_settings.EnableGlobalHotkeys) _hotkeyService.Unregister();

        if (result.Success)
        {
            Log(result.Message, DiagnosticLevel.Info);
            return;
        }

        Log($"Hotkey registration failed: {result.Message}", DiagnosticLevel.Critical);
        if (showUserError)
        {
            var rollback = result.PreviousBindingsRestored ? "\n\nThe previous shortcuts remain active." : "";
            MessageBox.Show(result.Message + rollback, "Global Hotkeys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowPrimarySurface()
    {
        if (_deviceSelectionDialog != null) { _deviceSelectionDialog.Activate(); return; }
        if (_authDialog != null) { _authDialog.Activate(); return; }
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        EnsureFlyout()?.ShowFlyout(TryGetTrayAnchor());
    }

    private void TogglePrimarySurface(TrayActivation activation)
    {
        if (_trayMenuCoordinator.TryDefer(activation))
        {
            _trayMenu?.Dismiss();
            return;
        }

        if (_deviceSelectionDialog != null) { _deviceSelectionDialog.Activate(); return; }
        if (_authDialog != null) { _authDialog.Activate(); return; }
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        EnsureFlyout()?.ToggleFromTray(activation, TryGetTrayAnchor());
    }

    private FlyoutWindow? EnsureFlyout()
    {
        if (_flyout != null) return _flyout;
        if (_engine == null || _isShuttingDown) return null;

        var flyout = new FlyoutWindow(
            _engine,
            _settings,
            () => ShowAuthDialog(restartEngine: true),
            () => _ = ChooseSoundbarAsync());
        flyout.UpdateState(_latestState);
        _flyout = flyout;
        return flyout;
    }

    private void ShowTrayContextMenu()
    {
        if (_isShuttingDown) return;
        if (_trayMenu?.IsVisible == true)
        {
            _trayMenu.Activate();
            return;
        }

        _flyout?.ResolveTrayInteraction();
        var menu = EnsureTrayMenu();
        _trayMenuCoordinator.Open();
        menu.UpdateState(_latestState);

        try
        {
            menu.ShowMenu(TryGetTrayAnchor());
        }
        catch
        {
            _trayMenuCoordinator.Close();
            try { menu.Dismiss(); } catch { }
            throw;
        }
    }

    private TrayMenuWindow EnsureTrayMenu()
    {
        if (_trayMenu != null) return _trayMenu;

        var menu = new TrayMenuWindow(
            ShowPrimarySurface,
            OpenSettingsWindow,
            () => ShowAuthDialog(restartEngine: true),
            ShutdownApp);
        menu.Dismissed += (_, _) => OnTrayMenuDismissed();
        _trayMenu = menu;
        return menu;
    }

    private void OnTrayMenuDismissed()
    {
        var pendingActivation = _trayMenuCoordinator.Close();
        if (pendingActivation is { } activation && !_isShuttingDown)
        {
            Dispatcher.BeginInvoke(
                () => TogglePrimarySurface(activation),
                System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            _trayIcon?.RestoreTrayFocus();
        }
    }

    private PixelRect? TryGetTrayAnchor()
    {
        return _trayIcon != null && _trayIcon.TryGetIconBounds(out var bounds)
            ? bounds
            : null;
    }

    private void OnEngineStateChanged(long generation, SoundbarState state)
    {
        if (generation != _stateRelay.Generation ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Log($"State updated: Connected={state.Connected}, Power={state.Power}, Vol={state.Volume}, Codec={state.Codec}, Ch={state.Channel}, Voice={state.VoiceMode}, Inp={state.Function}", DiagnosticLevel.Verbose);
        _stateRelay.Publish(generation, state);
    }

    private void ApplyStateToUi(SoundbarState state)
    {
        _latestState = state;
        var presentation = SoundbarUiPresentationFactory.Create(state);
        if (_trayIcon != null)
        {
            _trayIcon.UpdateIcon(IconHelper.GetTrayIcon(state.CodecBadgeKind), presentation.TrayTooltip);
        }

        // Nobody may open the flyout for days, so say so when the app needs the user, unless
        // they are already in the dialog that resolves it.
        if (_attentionNotifier.Update(state) is { } notification &&
            _authDialog == null && _deviceSelectionDialog == null &&
            _trayIcon?.ShowNotification(notification.Title, notification.Text) == true)
        {
            Log($"Tray notification shown: {notification.Attention}.", DiagnosticLevel.Info);
        }

        _trayMenu?.UpdateState(state);
        _flyout?.UpdateState(state);
    }

    private void ShutdownApp()
    {
        if (_isShuttingDown) return;
        Log("Shutting down application.", DiagnosticLevel.Info);
        CleanupApp();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("Application exited.", DiagnosticLevel.Info);
        CleanupApp();
        base.OnExit(e);
    }

    private void CleanupApp()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        CancelDeviceRecovery();
        try { StopTvFollow(); } catch { }
        try { _authDialog?.Close(); } catch { }
        try { _settingsWindow?.Close(); } catch { }
        try { _trayMenu?.CloseForShutdown(); } catch { }
        try { _hotkeyService?.Dispose(); } catch (Exception ex) { Log($"Hotkey cleanup warning: {ex.Message}", DiagnosticLevel.Info); }
        try { InvalidateEngineState(_engine); } catch { }
        try
        {
            _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"Engine cleanup warning: {ex.Message}", DiagnosticLevel.Info);
        }
        try { _flyout?.CloseForShutdown(); } catch (Exception ex) { Log($"Flyout cleanup warning: {ex.Message}", DiagnosticLevel.Info); }
        try { _trayIcon?.Dispose(); } catch (Exception ex) { Log($"Tray cleanup warning: {ex.Message}", DiagnosticLevel.Info); }
        try { IconHelper.DisposeCachedIcons(); } catch { }
        try
        {
            if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        }
        catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }

        _hotkeyService = null;
        _engine = null;
        _flyout = null;
        _trayMenu = null;
        _trayIcon = null;
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
    }
}
