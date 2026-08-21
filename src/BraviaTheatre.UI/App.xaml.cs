using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
    private NativeTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private SonyCredentialStore? _credentialStore;
    private SonyCredentials? _credentials;
    private AppSettings _settings = new();
    private SettingsWindow? _settingsWindow;
    private AuthDialog? _authDialog;
    private bool _isShuttingDown;
    private bool _startupCompleted;

    private readonly object _pendingStateLock = new();
    private PendingEngineState? _pendingState;
    private int _stateDispatchPending;

    private readonly record struct PendingEngineState(long Generation, SoundbarState State);

    private MenuItem? _headerMenuItem;

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

    public static void Log(string message, AppLogLevel level)
    {
        if (level > _currentLogLevel) return;

        lock (_logLock)
        {
            try
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                var logPath = Path.Combine(GetAppDataDir(), "bravia_csharp.log");

                if (File.Exists(logPath) && new FileInfo(logPath).Length > 2 * 1024 * 1024)
                {
                    try
                    {
                        var oldPath = Path.Combine(GetAppDataDir(), "bravia_csharp.log.old");
                        if (File.Exists(oldPath)) File.Delete(oldPath);
                        File.Move(logPath, oldPath);
                    }
                    catch { }
                }

                using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
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
            Log($"DISPATCHER UNHANDLED EXCEPTION ({args.Exception.GetType().Name}).");
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

        _settings = AppSettings.Load(out var settingsWarning);
        SetLogLevel(_settings.LogLevel);
        if (!string.IsNullOrWhiteSpace(settingsWarning))
            MessageBox.Show(settingsWarning, "Settings Warning", MessageBoxButton.OK, MessageBoxImage.Warning);

        _credentialStore = new SonyCredentialStore(Path.Combine(GetAppDataDir(), "credentials.dat"));
        var credentialResult = _credentialStore.Load();
        if (!string.IsNullOrWhiteSpace(credentialResult.Message))
            MessageBox.Show(credentialResult.Message, "Credential Storage", MessageBoxButton.OK, MessageBoxImage.Warning);

        _credentials = credentialResult.Credentials;
        if (_credentials?.IsValid != true)
        {
            Log("Valid protected credentials were not found. Opening AuthDialog...");
            if (!ShowAuthDialog(restartEngine: false))
            {
                Log("AuthDialog cancelled. Shutting down.");
                Shutdown();
                return;
            }
        }

        if (_credentials?.IsValid != true)
        {
            Log("No valid credentials after AuthDialog. Shutting down.");
            MessageBox.Show("Valid Sony credentials are required to run.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        Log("Credentials loaded successfully.");

        // Initialize System Tray
        InitializeTray();
        ReplaceEngine(_credentials);
        _startupCompleted = true;
    }

    private void InitializeTray()
    {
        Log("Initializing Native System Tray Icon...");
        _trayIcon = new NativeTrayIcon
        {
            LogAction = message => Log(message, AppLogLevel.Info),
            ShowAction = () =>
            {
                Log("Tray activated. Showing flyout...");
                ShowPrimarySurface();
            }
        };

        // Right-click context menu
        var menu = new ContextMenu();

        _headerMenuItem = new MenuItem
        {
            Header = "BRAVIA Theatre | Connecting...",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold
        };
        menu.Items.Add(_headerMenuItem);

        var openItem = new MenuItem { Header = "Open Quick Controls" };
        openItem.Click += (s, e) => ShowPrimarySurface();
        menu.Items.Add(openItem);

        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (s, e) => OpenSettingsWindow();
        menu.Items.Add(settingsItem);

        var setupItem = new MenuItem { Header = "Sony Account Setup…" };
        setupItem.Click += (s, e) => ShowAuthDialog(restartEngine: true);
        menu.Items.Add(setupItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) => ShutdownApp();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        if (_trayIcon.Show(IconHelper.GetTrayIcon("idle"), "BRAVIA Theatre PC"))
            Log("Native System Tray Icon shown.");
        else
            Log("Native System Tray Icon failed to initialize.");
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

            if (connectionChanged && _credentials?.IsValid == true)
            {
                ReplaceEngine(_credentials);
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
        if (_credentialStore == null) return false;
        if (_authDialog != null)
        {
            _authDialog.Activate();
            return false;
        }

        var dialog = new AuthDialog(_credentialStore);
        _authDialog = dialog;
        try
        {
            if (dialog.ShowDialog() != true || dialog.AuthenticatedCredentials?.IsValid != true)
                return false;

            _credentials = dialog.AuthenticatedCredentials;
            if (restartEngine) ReplaceEngine(_credentials);
            return true;
        }
        finally
        {
            _authDialog = null;
        }
    }

    private void ReplaceEngine(SonyCredentials credentials)
    {
        var oldEngine = _engine;
        var generation = InvalidateEngineState(oldEngine);
        ApplyStateToUi(SoundbarState.Disconnected);
        try { _hotkeyService?.Dispose(); } catch (Exception ex) { Log($"Hotkey cleanup warning: {ex.Message}"); }
        try { _flyout?.CloseForShutdown(); } catch (Exception ex) { Log($"Flyout cleanup warning: {ex.Message}"); }
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
        var newEngine = new BraviaEngine(credentials, host, port) { LogAction = Log };
        _engine = newEngine;
        _flyout = new FlyoutWindow(newEngine, _settings, OpenSettingsWindow);
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
        _flyout?.ShowFlyout();
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
        if (_trayIcon != null)
        {
            var tooltip = !state.Connected
                ? "BRAVIA Theatre PC • Disconnected — retrying..."
                : !state.Power
                    ? $"{state.DeviceName ?? "BRAVIA Theatre"} • Standby"
                    : $"{state.DeviceName ?? "BRAVIA Theatre"} • {state.HumanCodec} • Vol: {state.Volume}{(state.Mute ? " (Muted)" : "")}";
            _trayIcon.UpdateIcon(IconHelper.GetTrayIcon(state.CodecBadgeKind), tooltip);
        }

        if (_headerMenuItem != null)
        {
            _headerMenuItem.Header = !state.Connected
                ? "BRAVIA Theatre | Disconnected — retrying..."
                : state.Power ? $"{state.HumanCodec} | Vol: {state.Volume}" : "Standby";
        }

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
        _trayIcon = null;
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
    }
}
