using System;
using System.Diagnostics;

namespace BraviaTheatre.UI.Services;

public static class TaskbarSettingsService
{
    public const string TaskbarSettingsUri = "ms-settings:taskbar";

    public static bool TryOpenTaskbarSettings(out string? error) =>
        TryOpenTaskbarSettings(Process.Start, out error);

    internal static bool TryOpenTaskbarSettings(
        Func<ProcessStartInfo, Process?> launcher,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        error = null;

        try
        {
            using var process = launcher(new ProcessStartInfo(TaskbarSettingsUri)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not open Windows Taskbar settings: {ex.Message}";
            return false;
        }
    }
}
