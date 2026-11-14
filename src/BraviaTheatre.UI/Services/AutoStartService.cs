using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BraviaTheatre.UI.Services;

/// <summary>What the Run entry starts at sign-in, relative to the running copy.</summary>
internal enum RunEntryState
{
    None,
    ThisCopy,
    /// <summary>Another copy that still exists, for example a second install.</summary>
    OtherCopy,
    /// <summary>A copy that is gone, as after moving the executable.</summary>
    MissingCopy
}

public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegName = "BraviaTheatrePC";

    public static string GetExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "BraviaTheatrePC.exe";

    public static bool IsAutoStartEnabled() => InspectRunEntry().State == RunEntryState.ThisCopy;

    internal static (RunEntryState State, string? RegisteredPath) InspectRunEntry() =>
        Classify(ReadRunCommand(), GetExecutablePath(), File.Exists);

    internal static (RunEntryState State, string? RegisteredPath) Classify(
        string? registeredCommand,
        string executablePath,
        Func<string, bool> fileExists)
    {
        var registered = NormalizeCommand(registeredCommand);
        if (registered == null) return (RunEntryState.None, null);
        if (string.Equals(registered, executablePath, StringComparison.OrdinalIgnoreCase))
            return (RunEntryState.ThisCopy, registered);
        return (fileExists(registered) ? RunEntryState.OtherCopy : RunEntryState.MissingCopy, registered);
    }

    /// <summary>
    /// Points a Run entry that names a vanished executable at this copy. Moving the app left the
    /// old entry behind: Settings showed Start with Windows off, and nothing started at sign-in.
    /// </summary>
    /// <returns>True when the entry was repointed; false when nothing was done, or on an error.</returns>
    public static bool TryRepairMovedEntry(out string? error)
    {
        error = null;
        if (InspectRunEntry().State != RunEntryState.MissingCopy
            || IsTemporaryLocation(GetExecutablePath(), Path.GetTempPath()))
        {
            return false;
        }

        return TrySetAutoStart(true, out error);
    }

    public static bool TrySetAutoStart(bool enable, out string? error)
    {
        error = null;
        string? command = null;
        if (enable)
        {
            var executablePath = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                error = "Windows could not determine the application executable path.";
                return false;
            }
            command = $"\"{executablePath}\"";
        }

        return TryWriteRunCommand(command, out error);
    }

    /// <summary>The Run entry's raw command, so it can be put back exactly as it was.</summary>
    internal static string? ReadRunCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppRegName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the Run entry's command; null removes the entry.</summary>
    internal static bool TryWriteRunCommand(string? command, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (command != null)
                key.SetValue(AppRegName, command, RegistryValueKind.String);
            else
                key.DeleteValue(AppRegName, false);
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
