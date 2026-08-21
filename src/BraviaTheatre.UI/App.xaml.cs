using System;
using System.IO;
using System.Text.Json;
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

    private BraviaEngine? _engine;
    private FlyoutWindow? _flyout;
    private NativeTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private AppSettings _settings = new();

    private MenuItem? _headerMenuItem;

    private string _keysPath = "session_keys.json";
    public static string GetAppDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BraviaTheatrePC");
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

    protected override void OnStartup(StartupEventArgs e)
    {
        // Prevent application from closing when modal dialogs close
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log($"FATAL UNHANDLED EXCEPTION: {args.ExceptionObject}");
            MessageBox.Show($"Fatal error:\n\n{args.ExceptionObject}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            if (args.Exception is OperationCanceledException or ObjectDisposedException)
            {
                args.Handled = true;
                return;
            }
            Log($"DISPATCHER UNHANDLED EXCEPTION: {args.Exception}");
            MessageBox.Show($"Dispatcher error:\n\n{args.Exception.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Log("Application starting...");

        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log("Another instance is already running.");
            MessageBox.Show("BRAVIA Theatre PC is already running in the system tray.", "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        SetLogLevel(_settings.LogLevel);

        // Locate session_keys.json (check next to EXE first, then AppData, then dev parent)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var localKeys = Path.Combine(exeDir, "session_keys.json");
        var appDataKeys = Path.Combine(GetAppDataDir(), "session_keys.json");

        if (File.Exists(localKeys))
        {
            _keysPath = localKeys;
        }
        else if (File.Exists(appDataKeys))
        {
            _keysPath = appDataKeys;
        }
        else
        {
            var parentKeys = Path.Combine(exeDir, "..", "..", "..", "session_keys.json");
            if (File.Exists(parentKeys))
            {
                _keysPath = Path.GetFullPath(parentKeys);
            }
            else
            {
                _keysPath = appDataKeys;
            }
        }
        Log($"Using session keys path: {_keysPath}");

        var creds = SonyCredentials.LoadFromFile(_keysPath);
        if (creds == null || string.IsNullOrEmpty(creds.HmacKey))
        {
            Log("Credentials not found or missing HmacKey. Opening AuthDialog...");
            var authDlg = new AuthDialog(_keysPath);
            var result = authDlg.ShowDialog();
            if (result != true)
            {
                Log("AuthDialog cancelled. Shutting down.");
                Shutdown();
                return;
            }
            creds = SonyCredentials.LoadFromFile(_keysPath);
        }

        if (creds == null || string.IsNullOrEmpty(creds.HmacKey))
        {
            Log("No valid credentials after AuthDialog. Shutting down.");
            MessageBox.Show("Valid Sony credentials are required to run.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        Log("Credentials loaded successfully.");

        // Static host/port from settings or config.json
        string? host = string.IsNullOrWhiteSpace(_settings.StaticHost) ? null : _settings.StaticHost;
        int port = _settings.StaticPort > 0 ? _settings.StaticPort : 55051;

        // Initialize Core Engine
        _engine = new BraviaEngine(creds, host, port)
        {
            LogAction = Log
        };

        // Initialize Flyout
        _flyout = new FlyoutWindow(_engine, _settings, OpenSettingsWindow);

        // Initialize Global Hotkeys
        _hotkeyService = new GlobalHotkeyService(_engine);
        if (_settings.EnableGlobalHotkeys)
        {
            try
            {
                _hotkeyService.Register(_settings);
                Log("Global hotkeys registered.");
            }
            catch (Exception ex)
            {
                Log($"Hotkey registration warning: {ex.Message}");
            }
        }

        // Initialize System Tray
        InitializeTray();

        _engine.StateChanged += OnEngineStateChanged;
        _engine.Start();
        Log("Engine started and running in system tray.");
    }

    private void InitializeTray()
    {
        Log("Initializing Native System Tray Icon...");
        _trayIcon = new NativeTrayIcon
        {
            LeftClickAction = () =>
            {
                Log("Tray left-clicked. Toggling flyout...");
                _flyout?.ToggleFlyout();
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
        openItem.Click += (s, e) => _flyout?.ToggleFlyout();
        menu.Items.Add(openItem);

        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (s, e) => OpenSettingsWindow();
        menu.Items.Add(settingsItem);

        var setupItem = new MenuItem { Header = "Sony Account Setup…" };
        setupItem.Click += (s, e) =>
        {
            var authDlg = new AuthDialog(_keysPath);
            authDlg.ShowDialog();
        };
        menu.Items.Add(setupItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) => ShutdownApp();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        _trayIcon.Show(IconHelper.GetTrayIcon("idle"), "BRAVIA Theatre PC");
        Log("Native System Tray Icon shown.");
    }

    private void OpenSettingsWindow()
    {
        var win = new SettingsWindow(_settings, () =>
        {
            var authDlg = new AuthDialog(_keysPath);
            authDlg.ShowDialog();
        });

        win.SettingsSaved += (updated) =>
        {
            _settings = updated;
            SetLogLevel(_settings.LogLevel);
            _flyout?.ApplySettings(_settings);

            if (_settings.EnableGlobalHotkeys)
            {
                _hotkeyService?.Register(_settings);
            }
            else
            {
                _hotkeyService?.Unregister();
            }
        };

        win.ShowDialog();
    }

    private void OnEngineStateChanged(SoundbarState state)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        Log($"State updated: Connected={state.Connected}, Power={state.Power}, Vol={state.Volume}, Codec={state.Codec}, Ch={state.Channel}, Voice={state.VoiceMode}, Inp={state.Function}", AppLogLevel.Verbose);
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

                if (_trayIcon != null)
                {
                    _trayIcon.UpdateIcon(IconHelper.GetTrayIcon(state.CodecBadgeKind), state.HumanCodec);
                }

                if (_headerMenuItem != null)
                {
                    _headerMenuItem.Header = state.Power ? $"{state.HumanCodec} | Vol: {state.Volume}" : "Standby";
                }

                _flyout?.UpdateState(state);
            });
        }
        catch { }
    }

    private void ShutdownApp()
    {
        Log("Shutting down application.");
        try
        {
            _hotkeyService?.Dispose();
            _engine?.Dispose();
            _trayIcon?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("Application exited.");
        try
        {
            _hotkeyService?.Dispose();
            _engine?.Dispose();
            _trayIcon?.Dispose();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
