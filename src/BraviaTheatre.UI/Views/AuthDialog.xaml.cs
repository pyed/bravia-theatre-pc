using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using BraviaTheatre.Core.Auth;

namespace BraviaTheatre.UI.Views;

public partial class AuthDialog : Window
{
    private const string ClientId = "b9795d31-4179-43c3-8f04-94cb5c8a4dfa";
    private const string RedirectUri = "ssh-app://signin";
    private const string Scope = "openid,profile";

    private readonly string _keysPath;

    public AuthDialog(string keysPath)
    {
        InitializeComponent();
        _keysPath = keysPath;
    }

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var authUrl = $"https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/authorize?response_type=code&client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scope)}";
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
    }

    private void BtnComplete_Click(object sender, RoutedEventArgs e)
    {
        var input = TxtAuthCode.Text.Trim();
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show("Please enter the authorization code or redirect URL.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string code = input;
        if (input.Contains("code="))
        {
            var uri = new Uri(input.Replace("ssh-app://", "https://"));
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            code = query.Get("code") ?? input;
        }

        BtnComplete.IsEnabled = false;
        BtnComplete.Content = "Authenticating...";

        try
        {
            // If existing session keys file exists, load hmac key; otherwise create template
            var creds = SonyCredentials.LoadFromFile(_keysPath) ?? new SonyCredentials
            {
                ClientId = ClientId,
                SessionId = Guid.NewGuid().ToString(),
                ***REMOVED***
            };

            creds.AccessToken = code;
            creds.SaveToFile(_keysPath);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save credentials: {ex.Message}", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
