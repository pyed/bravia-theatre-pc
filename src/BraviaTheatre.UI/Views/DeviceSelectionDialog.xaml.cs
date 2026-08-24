using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.UI.Views;

public partial class DeviceSelectionDialog : Window
{
    public DeviceSelectionDialog(IReadOnlyList<SonyDeviceInfo> devices)
    {
        InitializeComponent();
        WindowBackdropService.Attach(this, WindowBackdropKind.MainWindow);
        DeviceList.ItemsSource = devices;
        if (devices.Count > 0) DeviceList.SelectedIndex = 0;
    }

    public string? SelectedDeviceId =>
        (DeviceList.SelectedItem as SonyDeviceInfo)?.DeviceId;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDeviceId == null) return;
        DialogResult = true;
    }

    private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedDeviceId == null) return;
        DialogResult = true;
    }
}
