using System;
using System.Windows;
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
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = ChkStartWithWindows.IsChecked ?? false;
        _settings.AlwaysShowOnTaskbar = ChkAlwaysShowOnTaskbar.IsChecked ?? true;
        _settings.ShowRearSpeaker = ChkShowRearSpeaker.IsChecked ?? false;
        _settings.EnableGlobalHotkeys = ChkEnableGlobalHotkeys.IsChecked ?? true;

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
