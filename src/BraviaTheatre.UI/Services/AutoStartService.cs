using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BraviaTheatre.UI.Services;

public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegName = "BraviaTheatrePC";

    public static string GetExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "BraviaTheatrePC.exe";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var registered = NormalizeCommand(key?.GetValue(AppRegName) as string);
            return string.Equals(registered, GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetAutoStart(bool enable, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (enable)
            {
                var executablePath = GetExecutablePath();
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    error = "Windows could not determine the application executable path.";
                    return false;
                }
                key.SetValue(AppRegName, $"\"{executablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppRegName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not update Windows startup settings: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// True when the executable runs from inside the temp folder, as it does when started straight
    /// from the release zip. A Run entry for that path would start nothing once the folder is gone.
    /// </summary>
    internal static bool IsTemporaryLocation(string executablePath, string tempPath)
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempPath)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(executablePath).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }
        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }
}
