using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BraviaTheatre.UI.Services;

public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string NotifyKeyPath = @"Control Panel\NotifyIconSettings";
    private const string AppRegName = "BraviaTheatrePC";

    public static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "BraviaTheatrePC.exe";
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var val = key?.GetValue(AppRegName) as string;
            return !string.IsNullOrEmpty(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enable)
            {
                var exePath = GetExecutablePath();
                key.SetValue(AppRegName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AppRegName, false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsTrayPromoted()
    {
        var target = Path.GetFileName(GetExecutablePath()).ToLowerInvariant();

        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(NotifyKeyPath, false);
            if (rootKey == null) return false;

            foreach (var subName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subName, false);
                if (subKey == null) continue;

                var exePath = subKey.GetValue("ExecutablePath") as string;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var lower = exePath.ToLowerInvariant();
                    if (lower.Contains("braviatheatre") || Path.GetFileName(lower) == target)
                    {
                        var isPromoted = subKey.GetValue("IsPromoted");
                        if (isPromoted is int val && val == 1)
                            return true;
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        return false;
    }

    public static bool SetTrayPromoted(bool enable)
    {
        var target = Path.GetFileName(GetExecutablePath()).ToLowerInvariant();
        int val = enable ? 1 : 0;
        bool updated = false;

        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(NotifyKeyPath, true);
            if (rootKey == null) return false;

            foreach (var subName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subName, true);
                if (subKey == null) continue;

                var exePath = subKey.GetValue("ExecutablePath") as string;
                if (!string.IsNullOrEmpty(exePath))
                {
                    var lower = exePath.ToLowerInvariant();
                    if (lower.Contains("braviatheatre") || Path.GetFileName(lower) == target)
                    {
                        subKey.SetValue("IsPromoted", val, RegistryValueKind.DWord);
                        updated = true;
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        return updated;
    }
}
