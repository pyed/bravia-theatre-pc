using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using BraviaTheatre.Core.Auth;
using Microsoft.Web.WebView2.Core;

namespace BraviaTheatre.UI.Views;

public partial class AuthDialog : Window
{
    private readonly string _keysPath;
    private string? _codeVerifier;
    private string? _expectedState;
    private string? _authUrl;
    private bool _isManualMode = false;
    private bool _isProcessing = false;

    public AuthDialog(string keysPath)
    {
        InitializeComponent();
        _keysPath = keysPath;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Generate PKCE credentials for this OAuth session
        var (authUrl, codeVerifier, state) = SonyOAuth.StartOAuthLogin();
        _authUrl = authUrl;
        _codeVerifier = codeVerifier;
        _expectedState = state;

        await InitializeAutoLoginAsync();
    }

    private async Task InitializeAutoLoginAsync()
    {
        try
        {
            OverlayProgress.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "Loading Sony Sign-In...";

            var webViewDataFolder = Path.Combine(App.GetAppDataDir(), "WebView2");
            if (!Directory.Exists(webViewDataFolder))
            {
                try { Directory.CreateDirectory(webViewDataFolder); } catch { }
            }

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataFolder);
            await AuthWebView.EnsureCoreWebView2Async(env);

            AuthWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            AuthWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            // Intercept the custom scheme redirect callback
            AuthWebView.CoreWebView2.NavigationStarting += async (s, args) =>
            {
                if (args.Uri.StartsWith("ssh-app://signin", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true; // Prevent standard navigation error page
                    await ProcessAuthCallbackAsync(args.Uri);
                }
            };

            AuthWebView.NavigationCompleted += (s, args) =>
            {
                if (!_isProcessing)
                {
                    OverlayProgress.Visibility = Visibility.Collapsed;
                }
            };

            AuthWebView.Source = new Uri(_authUrl!);
        }
        catch (Exception ex)
        {
            App.Log($"WebView2 initialization failed, falling back to manual mode: {ex.Message}");
            SwitchToManualMode();
        }
    }

    private async Task ProcessAuthCallbackAsync(string codeOrUrl)
    {
        if (_isProcessing) return;
        _isProcessing = true;

        if (string.IsNullOrWhiteSpace(codeOrUrl))
        {
            MessageBox.Show("Please enter the authorization code or redirect URL.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            _isProcessing = false;
            return;
        }

        if (string.IsNullOrEmpty(_codeVerifier))
        {
            MessageBox.Show("OAuth login session is not initialized.", "Session Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            _isProcessing = false;
            return;
        }

        OverlayProgress.Visibility = Visibility.Visible;
        TxtProgressStatus.Text = "Exchanging session keys & connecting...";
        BtnManualComplete.IsEnabled = false;

        try
        {
            var creds = await SonyOAuth.CompleteOAuthFlowAsync(codeOrUrl, _codeVerifier, _expectedState);
            creds.SaveToFile(_keysPath);

            App.Log("OAuth flow and local key exchange completed successfully.");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _isProcessing = false;
            OverlayProgress.Visibility = Visibility.Collapsed;
            BtnManualComplete.IsEnabled = true;

            MessageBox.Show($"Authentication and key exchange failed:\n\n{ex.Message}", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_authUrl))
            {
                var (authUrl, codeVerifier, state) = SonyOAuth.StartOAuthLogin();
                _authUrl = authUrl;
                _codeVerifier = codeVerifier;
                _expectedState = state;
            }

            Process.Start(new ProcessStartInfo(_authUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch sign-in URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnComplete_Click(object sender, RoutedEventArgs e)
    {
        await ProcessAuthCallbackAsync(TxtAuthCode.Text.Trim());
    }

    private void BtnToggleMode_Click(object sender, RoutedEventArgs e)
    {
        if (_isManualMode)
        {
            SwitchToAutoMode();
        }
        else
        {
            SwitchToManualMode();
        }
    }

    private void SwitchToManualMode()
    {
        _isManualMode = true;
        PanelAutoMode.Visibility = Visibility.Collapsed;
        PanelManualMode.Visibility = Visibility.Visible;
        BtnManualComplete.Visibility = Visibility.Visible;
        BtnToggleMode.Content = "Switch back to Automatic Sign-In";
        TxtSubtitle.Text = "Manual mode: Open login in browser and paste the authorization callback URL";
    }

    private void SwitchToAutoMode()
    {
        _isManualMode = false;
        PanelManualMode.Visibility = Visibility.Collapsed;
        PanelAutoMode.Visibility = Visibility.Visible;
        BtnManualComplete.Visibility = Visibility.Collapsed;
        BtnToggleMode.Content = "Switch to Manual Mode (F12)";
        TxtSubtitle.Text = "Sign in with your Sony account to connect your BRAVIA Theatre system";
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
