using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.Core.Models;
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

    private MenuItem? _headerMenuItem;
    private MenuItem? _autoStartMenuItem;
    private MenuItem? _promoteMenuItem;

    private string _keysPath = "session_keys.json";

    public static void Log(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            File.AppendAllText("bravia_csharp.log", line + Environment.NewLine);
        }
        catch
        {
            // Ignore logging errors
        }
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

        // Locate session_keys.json
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _keysPath = Path.Combine(baseDir, "session_keys.json");
        if (!File.Exists(_keysPath))
        {
            var parentKeys = Path.Combine(baseDir, "..", "..", "..", "session_keys.json");
            if (File.Exists(parentKeys))
            {
                _keysPath = Path.GetFullPath(parentKeys);
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

        // Optional config.json
        string? host = null;
        int port = 55051;
        var configPath = Path.Combine(baseDir, "config.json");
        if (!File.Exists(configPath))
        {
            var parentConfig = Path.Combine(baseDir, "..", "..", "..", "config.json");
            if (File.Exists(parentConfig)) configPath = Path.GetFullPath(parentConfig);
        }

        if (File.Exists(configPath))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("host", out var hProp) && !string.IsNullOrEmpty(hProp.GetString()))
                {
                    host = hProp.GetString();
                }
                if (doc.RootElement.TryGetProperty("port", out var pProp))
                {
                    port = pProp.GetInt32();
                }
                Log($"Config loaded: Host={host}, Port={port}");
            }
            catch (Exception ex)
            {
                Log($"Config read error: {ex.Message}");
            }
        }

        // Initialize Core Engine
        _engine = new BraviaEngine(creds, host, port);

        // Initialize Flyout
        _flyout = new FlyoutWindow(_engine);

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

        _autoStartMenuItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = AutoStartService.IsAutoStartEnabled()
        };
        _autoStartMenuItem.Click += (s, e) =>
        {
            bool next = !AutoStartService.IsAutoStartEnabled();
            AutoStartService.SetAutoStart(next);
            _autoStartMenuItem.IsChecked = next;
        };
        menu.Items.Add(_autoStartMenuItem);

        _promoteMenuItem = new MenuItem
        {
            Header = "Always show on taskbar",
            IsCheckable = true,
            IsChecked = AutoStartService.IsTrayPromoted()
        };
        _promoteMenuItem.Click += (s, e) =>
        {
            bool next = !AutoStartService.IsTrayPromoted();
            AutoStartService.SetTrayPromoted(next);
            _promoteMenuItem.IsChecked = next;
        };
        menu.Items.Add(_promoteMenuItem);

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

    private void OnEngineStateChanged(SoundbarState state)
    {
        Log($"State updated: Connected={state.Connected}, Power={state.Power}, Vol={state.Volume}, Codec={state.Codec}, Ch={state.Channel}");
        Dispatcher.Invoke(() =>
        {
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

    private void ShutdownApp()
    {
        Log("Shutting down application.");
        _engine?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("Application exited.");
        _engine?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
