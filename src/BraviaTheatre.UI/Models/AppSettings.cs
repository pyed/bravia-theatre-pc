using System;
using System.IO;
using System.Text.Json;

namespace BraviaTheatre.UI.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool ShowRearSpeaker { get; set; }
    public bool EnableGlobalHotkeys { get; set; } = true;

    public string HotkeyVolumeUp { get; set; } = "Ctrl + Alt + Up";
    public string HotkeyVolumeDown { get; set; } = "Ctrl + Alt + Down";
    public string HotkeyMute { get; set; } = "Ctrl + Shift + M";
    public string HotkeySoundField { get; set; } = "Ctrl + Alt + S";
    public string HotkeyVoiceMode { get; set; } = "Ctrl + Alt + V";
    public string HotkeyNightMode { get; set; } = "Ctrl + Alt + N";

    public string? StaticHost { get; set; } = "";
    public int StaticPort { get; set; } = 55051;
    public string LogLevel { get; set; } = "Critical";

    public static string SettingsFilePath => Path.Combine(App.GetAppDataDir(), "settings.json");

    public static AppSettings Load() => Load(out _);

    public static AppSettings Load(out string? warning)
    {
        warning = null;
        if (File.Exists(SettingsFilePath))
        {
            if (TryRead(SettingsFilePath, out var loaded, out var error))
                return loaded!;

            warning = error;
            return new AppSettings();
        }

        return new AppSettings();
    }

    public bool TrySave(out string? error)
    {
        error = null;
        StaticHost = StaticHost?.Trim() ?? "";
        if (StaticPort is < 1 or > 65535)
        {
            error = "The connection port must be between 1 and 65535.";
            return false;
        }

        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            tempPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Could not save settings: {ex.Message}";
            return false;
        }
        finally
        {
            if (tempPath != null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
        }
    }

    public void Save() => TrySave(out _);

    private static bool TryRead(string path, out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (settings == null) throw new JsonException("The settings document is empty.");
            settings.StaticPort = settings.StaticPort is >= 1 and <= 65535 ? settings.StaticPort : 55051;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"Could not load settings from {path}: {ex.Message}";
            return false;
        }
    }
}
