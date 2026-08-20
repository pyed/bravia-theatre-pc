using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BraviaTheatre.UI.Models;
using Microsoft.Win32;

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

        ChkStartWithWindows.IsChecked = _settings.StartWithWindows;
        ChkAlwaysShowOnTaskbar.IsChecked = _settings.AlwaysShowOnTaskbar;
        ChkShowRearSpeaker.IsChecked = _settings.ShowRearSpeaker;
        ChkEnableGlobalHotkeys.IsChecked = _settings.EnableGlobalHotkeys;

        TxtHotkeyVolUp.Text = _settings.HotkeyVolumeUp;
        TxtHotkeyVolDown.Text = _settings.HotkeyVolumeDown;
        TxtHotkeyMute.Text = _settings.HotkeyMute;
        TxtHotkeySoundField.Text = _settings.HotkeySoundField;
        TxtHotkeyVoiceMode.Text = _settings.HotkeyVoiceMode;
        TxtHotkeyNightMode.Text = _settings.HotkeyNightMode;
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
        TxtHotkeyMute.Text = "Ctrl + Alt + M";
        TxtHotkeySoundField.Text = "Ctrl + Alt + S";
        TxtHotkeyVoiceMode.Text = "Ctrl + Alt + V";
        TxtHotkeyNightMode.Text = "Ctrl + Alt + N";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = ChkStartWithWindows.IsChecked ?? false;
        _settings.AlwaysShowOnTaskbar = ChkAlwaysShowOnTaskbar.IsChecked ?? true;
        _settings.ShowRearSpeaker = ChkShowRearSpeaker.IsChecked ?? false;
        _settings.EnableGlobalHotkeys = ChkEnableGlobalHotkeys.IsChecked ?? true;

        _settings.HotkeyVolumeUp = string.IsNullOrWhiteSpace(TxtHotkeyVolUp.Text) ? "Ctrl + Alt + Up" : TxtHotkeyVolUp.Text;
        _settings.HotkeyVolumeDown = string.IsNullOrWhiteSpace(TxtHotkeyVolDown.Text) ? "Ctrl + Alt + Down" : TxtHotkeyVolDown.Text;
        _settings.HotkeyMute = string.IsNullOrWhiteSpace(TxtHotkeyMute.Text) ? "Ctrl + Alt + M" : TxtHotkeyMute.Text;
        _settings.HotkeySoundField = string.IsNullOrWhiteSpace(TxtHotkeySoundField.Text) ? "Ctrl + Alt + S" : TxtHotkeySoundField.Text;
        _settings.HotkeyVoiceMode = string.IsNullOrWhiteSpace(TxtHotkeyVoiceMode.Text) ? "Ctrl + Alt + V" : TxtHotkeyVoiceMode.Text;
        _settings.HotkeyNightMode = string.IsNullOrWhiteSpace(TxtHotkeyNightMode.Text) ? "Ctrl + Alt + N" : TxtHotkeyNightMode.Text;

        ApplyStartupRegistry(_settings.StartWithWindows);

        _settings.Save();
        SettingsSaved?.Invoke(_settings);
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

    private static void ApplyStartupRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            string exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            if (enable)
            {
                key.SetValue("BraviaTheatrePC", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("BraviaTheatrePC", false);
            }
        }
        catch { }
    }
}
