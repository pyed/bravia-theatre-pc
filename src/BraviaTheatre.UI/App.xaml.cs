using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;
using BraviaTheatre.UI.Views;

namespace BraviaTheatre.UI;

public partial class App : Application
{
    private const string MutexName = "BraviaTheatrePC_SingleInstance_Mutex_2026";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    private BraviaEngine? _engine;
    private Action<SoundbarState>? _engineStateChangedHandler;
    private long _engineGeneration;
    private FlyoutWindow? _flyout;
    private TrayMenuWindow? _trayMenu;
    private NativeTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private SonyCredentialLifecycle? _credentialLifecycle;
    private AppSettings _settings = new();
    private SettingsWindow? _settingsWindow;
    private AuthDialog? _authDialog;
    private bool _isShuttingDown;
    private bool _startupCompleted;

    private readonly object _pendingStateLock = new();
    private PendingEngineState? _pendingState;
    private int _stateDispatchPending;
    private SoundbarState _latestState = SoundbarState.Disconnected;
    private readonly TrayContextMenuCoordinator _trayMenuCoordinator = new();

    private readonly record struct PendingEngineState(long Generation, SoundbarState State);

    public static string GetAppDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraviaTheatrePC");
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }

    public enum AppLogLevel
    {
        Critical = 0,
        Info = 1,
        Verbose = 2
    }

    private static AppLogLevel _currentLogLevel = AppLogLevel.Critical;

    public static void SetLogLevel(string? levelStr)
    {
        _currentLogLevel = levelStr?.ToLowerInvariant() switch
        {
            "verbose" => AppLogLevel.Verbose,
            "info" => AppLogLevel.Info,
            _ => AppLogLevel.Critical
        };
    }

    private static readonly object _logLock = new();
    private static DateTime _lastLogMaintenanceDate = DateTime.MinValue;

    public static void Log(string message, AppLogLevel level)
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

    public static void Log(string message)
    {
        bool isCritical = message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("failed", StringComparison.OrdinalIgnoreCase);

        bool isInfo = message.Contains("Starting", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("Discovered", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("handshake completed", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("stream started", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("Connecting", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("Credentials loaded", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("Global hotkeys registered", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("Tray Icon shown", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("Shutting down", StringComparison.OrdinalIgnoreCase);

        var level = isCritical ? AppLogLevel.Critical : (isInfo ? AppLogLevel.Info : AppLogLevel.Verbose);
        Log(message, level);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Prevent application from closing when modal dialogs close
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var failureType = args.ExceptionObject?.GetType().Name ?? "UnknownFailure";
            Log($"FATAL UNHANDLED EXCEPTION ({failureType}).");
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
            Log($"DISPATCHER UNHANDLED EXCEPTION: {FormatExceptionForLog(args.Exception)}");
            MessageBox.Show("An application error occurred. Please try the action again.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            if (!_startupCompleted)
                Dispatcher.BeginInvoke(ShutdownApp);
        };

        Log("Application starting...");

        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            Log("Another instance is already running. Signaling Quick Settings flyout.");
            uint wmShowFlyout = RegisterWindowMessage("BRAVIA_THEATRE_PC_SHOW_FLYOUT");
            PostMessage(HWND_BROADCAST, wmShowFlyout, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

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
            static (credentials, checkpointRotatedRefreshTokenAsync, cancellationToken) =>
                SonyOAuth.RefreshSessionKeysAsync(
                    credentials,
                    cancellationToken,
                    checkpointRotatedRefreshTokenAsync: checkpointRotatedRefreshTokenAsync),
            (credentials, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!credentialStore.TrySave(credentials, out var error))
                    throw new InvalidOperationException(error ?? "Could not save protected Sony credentials.");
                return Task.CompletedTask;
            });

        if (_credentialLifecycle.CurrentCredentials?.IsValid != true)
        {
            Log("Valid protected credentials were not found. Opening AuthDialog...");
            if (!ShowAuthDialog(restartEngine: false))
            {
                Log("AuthDialog cancelled. Shutting down.");
                Shutdown();
                return;
            }
        }

        if (_credentialLifecycle.CurrentCredentials?.IsValid != true)
        {
            Log("No valid credentials after AuthDialog. Shutting down.");
            MessageBox.Show("Valid Sony credentials are required to run.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        Log("Credentials loaded successfully.");

        // Initialize System Tray
        var trayIconShown = InitializeTray();
        ReplaceEngine();
        _startupCompleted = true;
        if (trayIconShown)
            Dispatcher.BeginInvoke(new Action(ShowTrayIconGuidanceOnce));
    }

    private static string FormatExceptionForLog(Exception exception)
    {
        var details = new List<string>();
        for (Exception? current = exception; current != null && details.Count < 8; current = current.InnerException)
        {
            var message = current.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            details.Add(string.IsNullOrEmpty(message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {message}");
        }

        return string.Join(" -> ", details);
    }

    private bool InitializeTray()
    {
        Log("Initializing Native System Tray Icon...");
        _trayIcon = new NativeTrayIcon
        {
            LogAction = message => Log(message, AppLogLevel.Critical),
            ToggleAction = activation =>
            {
                Log("Tray activated. Toggling flyout...");
                TogglePrimarySurface(activation);
            },
            ShowAction = () =>
            {
                Log("Application activation requested. Showing flyout...");
                ShowPrimarySurface();
            },
            ContextMenuAction = ShowTrayContextMenu,
            IconRecreatedAction = () => _flyout?.RepositionIfVisible(TryGetTrayAnchor())
        };
        var shown = _trayIcon.Show(IconHelper.GetTrayIcon("idle"), "BRAVIA Theatre PC");
        if (shown)
            Log("Native System Tray Icon shown.");
        else
            Log("Native System Tray Icon failed to initialize.");
        return shown;
    }

    private void ShowTrayIconGuidanceOnce()
    {
        if (_isShuttingDown || _settings.TrayIconGuidanceShown) return;

        _settings.TrayIconGuidanceShown = true;
        if (!_settings.TrySave(out var saveError))
            Log(saveError ?? "Could not save the tray icon guidance preference.", AppLogLevel.Info);

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
        };

        win.ShowDialog();
    }

    private bool ShowAuthDialog(bool restartEngine)
    {
        if (_credentialLifecycle == null) return false;
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

    private void ReplaceEngine()
    {
        var credentialLifecycle = _credentialLifecycle
            ?? throw new InvalidOperationException("Sony credential lifecycle is not initialized.");
        if (credentialLifecycle.CurrentCredentials?.IsValid != true)
            throw new InvalidOperationException("Valid Sony credentials are required to start the engine.");

        var oldEngine = _engine;
        var generation = InvalidateEngineState(oldEngine);
        ApplyStateToUi(SoundbarState.Disconnected);
        try { _hotkeyService?.Dispose(); } catch (Exception ex) { Log($"Hotkey cleanup warning: {ex.Message}"); }
        try { _flyout?.CloseForShutdown(); } catch (Exception ex) { Log($"Flyout cleanup warning: {ex.Message}"); }
        _flyout = null;
        try
        {
            oldEngine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"Engine cleanup warning: {ex.Message}");
        }

        var host = string.IsNullOrWhiteSpace(_settings.StaticHost) ? null : _settings.StaticHost.Trim();
        var port = _settings.StaticPort is >= 1 and <= 65535 ? _settings.StaticPort : 55051;
        var newEngine = new BraviaEngine(credentialLifecycle, host, port) { LogAction = Log };
        _engine = newEngine;
        _hotkeyService = new GlobalHotkeyService(newEngine);
        ApplyHotkeySettings(showUserError: false);
        _engineStateChangedHandler = state => OnEngineStateChanged(generation, state);
        newEngine.StateChanged += _engineStateChangedHandler;
        newEngine.Start();
        Log("Engine started and running in system tray.");
    }

    private long InvalidateEngineState(BraviaEngine? engine)
    {
        var generation = Interlocked.Increment(ref _engineGeneration);
        var handler = _engineStateChangedHandler;
        _engineStateChangedHandler = null;
        lock (_pendingStateLock) _pendingState = null;
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
            Log(result.Message);
            return;
        }

        Log($"Hotkey registration failed: {result.Message}");
        if (showUserError)
        {
            var rollback = result.PreviousBindingsRestored ? "\n\nThe previous shortcuts remain active." : "";
            MessageBox.Show(result.Message + rollback, "Global Hotkeys", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowPrimarySurface()
    {
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
            () => ShowAuthDialog(restartEngine: true));
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
        if (generation != Volatile.Read(ref _engineGeneration) ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Log($"State updated: Connected={state.Connected}, Power={state.Power}, Vol={state.Volume}, Codec={state.Codec}, Ch={state.Channel}, Voice={state.VoiceMode}, Inp={state.Function}", AppLogLevel.Verbose);
        lock (_pendingStateLock)
        {
            if (generation != Volatile.Read(ref _engineGeneration)) return;
            _pendingState = new PendingEngineState(generation, state);
        }
        SchedulePendingStateDispatch();
    }

    private void SchedulePendingStateDispatch()
    {
        if (Interlocked.Exchange(ref _stateDispatchPending, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                PendingEngineState? latest;
                lock (_pendingStateLock)
                {
                    latest = _pendingState;
                    _pendingState = null;
                }
                Interlocked.Exchange(ref _stateDispatchPending, 0);
                if (latest is { } pending &&
                    pending.Generation == Volatile.Read(ref _engineGeneration) &&
                    !Dispatcher.HasShutdownStarted &&
                    !Dispatcher.HasShutdownFinished)
                {
                    ApplyStateToUi(pending.State);
                }

                lock (_pendingStateLock)
                {
                    if (_pendingState != null) SchedulePendingStateDispatch();
                }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _stateDispatchPending, 0);
        }
    }

    private void ApplyStateToUi(SoundbarState state)
    {
        _latestState = state;
        var presentation = SoundbarUiPresentationFactory.Create(state);
        if (_trayIcon != null)
        {
            _trayIcon.UpdateIcon(IconHelper.GetTrayIcon(state.CodecBadgeKind), presentation.TrayTooltip);
        }

        _trayMenu?.UpdateState(state);
        _flyout?.UpdateState(state);
    }

    private void ShutdownApp()
    {
        if (_isShuttingDown) return;
        Log("Shutting down application.");
        CleanupApp();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("Application exited.");
        CleanupApp();
        base.OnExit(e);
    }

    private void CleanupApp()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        try { _authDialog?.Close(); } catch { }
        try { _settingsWindow?.Close(); } catch { }
        try { _trayMenu?.CloseForShutdown(); } catch { }
        try { _hotkeyService?.Dispose(); } catch (Exception ex) { Log($"Hotkey cleanup warning: {ex.Message}"); }
        try { InvalidateEngineState(_engine); } catch { }
        try
        {
            _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"Engine cleanup warning: {ex.Message}");
        }
        try { _flyout?.CloseForShutdown(); } catch (Exception ex) { Log($"Flyout cleanup warning: {ex.Message}"); }
        try { _trayIcon?.Dispose(); } catch (Exception ex) { Log($"Tray cleanup warning: {ex.Message}"); }
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
