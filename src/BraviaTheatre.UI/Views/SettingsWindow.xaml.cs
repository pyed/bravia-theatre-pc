using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onReAuth;

    public event Action<AppSettings>? SettingsSaved;

    public SettingsWindow(AppSettings settings, Action onReAuth)
    {
        InitializeComponent();
        _settings = settings;
        _onReAuth = onReAuth;

        var ver = typeof(App).Assembly.GetName().Version;
        TxtAppVersion.Text = ver == null
            ? "BRAVIA Theatre PC"
            : $"BRAVIA Theatre PC v{ver.Major}.{ver.Minor}.{ver.Build}";

        ChkStartWithWindows.IsChecked = AutoStartService.IsAutoStartEnabled();
        ChkShowRearSpeaker.IsChecked = _settings.ShowRearSpeaker;
        ChkEnableGlobalHotkeys.IsChecked = _settings.EnableGlobalHotkeys;

        TxtHotkeyVolUp.Text = _settings.HotkeyVolumeUp;
        TxtHotkeyVolDown.Text = _settings.HotkeyVolumeDown;
        TxtHotkeyMute.Text = _settings.HotkeyMute;
        TxtHotkeySoundField.Text = _settings.HotkeySoundField;
        TxtHotkeyVoiceMode.Text = _settings.HotkeyVoiceMode;
        TxtHotkeyNightMode.Text = _settings.HotkeyNightMode;
        TxtStaticHost.Text = _settings.StaticHost ?? "";
        TxtStaticPort.Text = _settings.StaticPort.ToString();

        // Logging Level
        foreach (ComboBoxItem item in CboLogLevel.Items)
        {
            if (string.Equals(item.Tag?.ToString(), _settings.LogLevel, StringComparison.OrdinalIgnoreCase))
            {
                CboLogLevel.SelectedItem = item;
                break;
            }
        }
        if (CboLogLevel.SelectedItem == null) CboLogLevel.SelectedIndex = 0;

        TxtLogPath.Text = Path.Combine(App.GetAppDataDir(), "bravia_csharp.log");
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (sender is not TextBox box) return;

        Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (key is Key.Back or Key.Delete or Key.Escape)
        {
            box.Text = "";
            return;
        }

        var modifiers = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers.Add("Win");

        if (modifiers.Count == 0)
        {
            modifiers.Add("Ctrl");
            modifiers.Add("Alt");
        }

        string keyStr = key.ToString();
        if (keyStr.StartsWith("D") && keyStr.Length == 2 && char.IsDigit(keyStr[1]))
            keyStr = keyStr[1].ToString();

        modifiers.Add(keyStr);
        box.Text = string.Join(" + ", modifiers);
    }

    private void BtnResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        TxtHotkeyVolUp.Text = "Ctrl + Alt + Up";
        TxtHotkeyVolDown.Text = "Ctrl + Alt + Down";
        TxtHotkeyMute.Text = "Ctrl + Shift + M";
        TxtHotkeySoundField.Text = "Ctrl + Alt + S";
        TxtHotkeyVoiceMode.Text = "Ctrl + Alt + V";
        TxtHotkeyNightMode.Text = "Ctrl + Alt + N";
    }

    private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = App.GetAppDataDir();
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open logs folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtStaticPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Connection port must be a number between 1 and 65535.", "Invalid Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtStaticPort.Focus();
            return;
        }

        var updated = new AppSettings
        {
            StartWithWindows = ChkStartWithWindows.IsChecked ?? false,
            ShowRearSpeaker = ChkShowRearSpeaker.IsChecked ?? false,
            EnableGlobalHotkeys = ChkEnableGlobalHotkeys.IsChecked ?? true,
            HotkeyVolumeUp = TxtHotkeyVolUp.Text.Trim(),
            HotkeyVolumeDown = TxtHotkeyVolDown.Text.Trim(),
            HotkeyMute = TxtHotkeyMute.Text.Trim(),
            HotkeySoundField = TxtHotkeySoundField.Text.Trim(),
            HotkeyVoiceMode = TxtHotkeyVoiceMode.Text.Trim(),
            HotkeyNightMode = TxtHotkeyNightMode.Text.Trim(),
            StaticHost = TxtStaticHost.Text.Trim(),
            StaticPort = port,
            LogLevel = _settings.LogLevel
        };

        if (CboLogLevel.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag != null)
        {
            updated.LogLevel = selectedItem.Tag.ToString()!;
        }

        var validation = GlobalHotkeyService.ValidateSettings(updated);
        if (!validation.Success)
        {
            MessageBox.Show(this, validation.Message, "Invalid Global Hotkeys",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previousAutoStart = AutoStartService.IsAutoStartEnabled();
        if (!AutoStartService.TrySetAutoStart(updated.StartWithWindows, out var startupError))
        {
            MessageBox.Show(this, startupError, "Could Not Save Startup Setting",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!updated.TrySave(out var saveError))
        {
            AutoStartService.TrySetAutoStart(previousAutoStart, out _);
            MessageBox.Show(this, saveError, "Could Not Save Settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SettingsSaved?.Invoke(updated);
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BtnReAuth_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onReAuth.Invoke();
    }

    private void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/pyed/bravia-theatre-pc/releases") { UseShellExecute = true });
        }
        catch { }
    }

}
