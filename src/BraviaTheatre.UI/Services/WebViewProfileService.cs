using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BraviaTheatre.UI.Services;

internal static class WebViewProfileService
{
    private const int DeleteAttempts = 8;
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(350);

    internal static string GetRootDirectory(string appDataDirectory) =>
        Path.Combine(appDataDirectory, "WebView2");

    internal static bool DeleteStaleProfiles(string appDataDirectory) =>
        TryDeleteDirectory(GetRootDirectory(appDataDirectory));

    internal static string PrepareSessionDirectory(string appDataDirectory)
    {
        var root = GetRootDirectory(appDataDirectory);
        DeleteStaleProfiles(appDataDirectory);
        Directory.CreateDirectory(root);

        var sessionDirectory = Path.Combine(root, $"Auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionDirectory);
        return sessionDirectory;
    }

    internal static async Task CleanupSessionAsync(
        string? sessionDirectory,
        int? browserProcessId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory)) return;

        await WaitForBrowserExitAsync(browserProcessId, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryDeleteDirectory(sessionDirectory)) break;
            await Task.Delay(DeleteRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        var root = Path.GetDirectoryName(sessionDirectory);
        if (!string.IsNullOrWhiteSpace(root))
            TryDeleteIfEmpty(root);
    }

    private static async Task WaitForBrowserExitAsync(int? browserProcessId, CancellationToken cancellationToken)
    {
        if (browserProcessId is not > 0) return;

        try
        {
            using var process = Process.GetProcessById(browserProcessId.Value);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProcessExitTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The browser process has already exited.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Continue with retrying the directory deletion below.
        }
        catch (InvalidOperationException)
        {
            // The process exited between lookup and waiting.
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
