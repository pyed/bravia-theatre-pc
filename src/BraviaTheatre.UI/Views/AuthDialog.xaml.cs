using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using BraviaTheatre.Core.Auth;

namespace BraviaTheatre.UI.Views;

public partial class AuthDialog : Window
{
    private readonly string _keysPath;
    private string? _codeVerifier;
    private string? _expectedState;

    public AuthDialog(string keysPath)
    {
        InitializeComponent();
        _keysPath = keysPath;
    }

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (authUrl, codeVerifier, state) = SonyOAuth.StartOAuthLogin();
            _codeVerifier = codeVerifier;
            _expectedState = state;

            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch sign-in URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnComplete_Click(object sender, RoutedEventArgs e)
    {
        var input = TxtAuthCode.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show("Please enter the authorization code or redirect URL.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_codeVerifier))
        {
            MessageBox.Show("Please click 'Open Sony Sign-In in Browser' first to begin the login session.", "Session Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnComplete.IsEnabled = false;
        BtnComplete.Content = "Exchanging Keys...";

        try
        {
            var creds = await SonyOAuth.CompleteOAuthFlowAsync(input, _codeVerifier, _expectedState);
            creds.SaveToFile(_keysPath);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Authentication and key exchange failed:\n\n{ex.Message}", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnComplete.IsEnabled = true;
            BtnComplete.Content = "Complete & Connect";
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
