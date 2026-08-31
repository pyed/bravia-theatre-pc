using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BraviaTheatre.Core.Auth;
using BraviaTheatre.UI.Services;
using Microsoft.Web.WebView2.Core;

namespace BraviaTheatre.UI.Views;

public partial class AuthDialog : Window
{
    private readonly SonyCredentialLifecycle _credentialLifecycle;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private string? _codeVerifier;
    private string? _expectedState;
    private string? _authUrl;
    private bool _isManualMode = false;
    private bool _isProcessing = false;
    private bool _isClosed;
    private string? _webViewSessionDirectory;

    public AuthDialog(SonyCredentialLifecycle credentialLifecycle)
    {
        InitializeComponent();
        WindowBackdropService.Attach(this, WindowBackdropKind.MainWindow);
        _credentialLifecycle = credentialLifecycle;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Generate PKCE credentials for this OAuth session
        var (authUrl, codeVerifier, state) = SonyOAuth.StartOAuthLogin();
        _authUrl = authUrl;
        _codeVerifier = codeVerifier;
        _expectedState = state;

        try
        {
            await InitializeAutoLoginAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window is closing.
        }
    }

    private async Task InitializeAutoLoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            OverlayProgress.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "Loading Sony Sign-In...";

            _webViewSessionDirectory = WebViewProfileService.PrepareSessionDirectory(App.GetAppDataDir());
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _webViewSessionDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            await AuthWebView.EnsureCoreWebView2Async(env);
            cancellationToken.ThrowIfCancellationRequested();

            AuthWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            AuthWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            AuthWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            AuthWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            AuthWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;

            // Intercept the custom scheme redirect callback
            AuthWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            AuthWebView.NavigationCompleted += AuthWebView_NavigationCompleted;

            AuthWebView.Source = new Uri(_authUrl!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"WebView2 initialization failed ({ex.GetType().Name}); falling back to manual mode.");
            SwitchToManualMode();
        }
    }

    private async Task ProcessAuthCallbackAsync(string codeOrUrl)
    {
        if (_isProcessing) return;
        _isProcessing = true;

        if (string.IsNullOrWhiteSpace(codeOrUrl))
        {
            MessageBox.Show(this, "Please paste the complete Sony callback URL.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var creds = await SonyOAuth.CompleteOAuthFlowAsync(
                codeOrUrl,
                _codeVerifier,
                _expectedState,
                _lifetimeCts.Token,
                SelectDeviceAsync);

            var installResult = await _credentialLifecycle.InstallAsync(creds, _lifetimeCts.Token);
            if (installResult.Status != CredentialRenewalStatus.Succeeded)
            {
                throw new InvalidOperationException(
                    installResult.Diagnostic ?? "Could not save protected Sony credentials.");
            }

            App.Log("OAuth flow and local key exchange completed successfully.");
            if (_isClosed) return;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || _isClosed)
        {
            // Closing/canceling the dialog is not an authentication error.
        }
        catch (OperationCanceledException)
        {
            _isProcessing = false;
            OverlayProgress.Visibility = Visibility.Collapsed;
            BtnManualComplete.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _isProcessing = false;
            OverlayProgress.Visibility = Visibility.Collapsed;
            BtnManualComplete.IsEnabled = true;

            var detail = ex is InvalidOperationException or ArgumentException
                ? ex.Message
                : "Sony sign-in could not be completed. Please try again.";
            MessageBox.Show($"Authentication and key exchange failed:\n\n{detail}", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        catch (Exception)
        {
            MessageBox.Show("Failed to open the Sony sign-in page in your browser.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        _lifetimeCts.Cancel();
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _lifetimeCts.Cancel();
        int? browserProcessId = null;
        try
        {
            if (AuthWebView.CoreWebView2 != null)
            {
                browserProcessId = checked((int)AuthWebView.CoreWebView2.BrowserProcessId);
                AuthWebView.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
            }
            AuthWebView.NavigationCompleted -= AuthWebView_NavigationCompleted;
            AuthWebView.Dispose();
        }
        catch
        {
            // The WebView may be only partially initialized.
        }
        _lifetimeCts.Dispose();
        _ = CleanupWebViewProfileAsync(_webViewSessionDirectory, browserProcessId);
        base.OnClosed(e);
    }

    private static async Task CleanupWebViewProfileAsync(string? sessionDirectory, int? browserProcessId)
    {
        try
        {
            await WebViewProfileService.CleanupSessionAsync(sessionDirectory, browserProcessId);
        }
        catch (Exception ex)
        {
            App.Log($"WebView2 profile cleanup warning ({ex.GetType().Name}).", App.AppLogLevel.Info);
        }
    }

    private async void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ssh-app", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        args.Cancel = true;
        if (!uri.Host.Equals("signin", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Sony returned an unexpected callback address. Start sign-in again.",
                "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await ProcessAuthCallbackAsync(args.Uri);
    }

    private void AuthWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!_isProcessing && !_isClosed)
            OverlayProgress.Visibility = Visibility.Collapsed;
    }

    private Task<string?> SelectDeviceAsync(
        System.Collections.Generic.IReadOnlyList<SonyDeviceInfo> devices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => SelectDeviceAsync(devices, cancellationToken)).Task.Unwrap();
        }

        var dialog = new DeviceSelectionDialog(devices) { Owner = this };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.SelectedDeviceId : null);
    }
}
