using System;
using System.IO;
using System.Text.Json;

namespace BraviaTheatre.UI.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool AlwaysShowOnTaskbar { get; set; } = true;
    public bool ShowRearSpeaker { get; set; } = false;
    public bool EnableGlobalHotkeys { get; set; } = true;

    // Configurable Global Hotkeys (Default: Ctrl + Alt + Key)
    public string HotkeyVolumeUp { get; set; } = "Ctrl + Alt + Up";
    public string HotkeyVolumeDown { get; set; } = "Ctrl + Alt + Down";
    public string HotkeyMute { get; set; } = "Ctrl + Shift + M";
    public string HotkeySoundField { get; set; } = "Ctrl + Alt + S";
    public string HotkeyVoiceMode { get; set; } = "Ctrl + Alt + V";
    public string HotkeyNightMode { get; set; } = "Ctrl + Alt + N";

    public string? StaticHost { get; set; } = "";
    public int StaticPort { get; set; } = 55051;

    private static string GetSettingsFilePath()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var localPath = Path.Combine(exeDir, "settings.json");
        if (File.Exists(localPath)) return localPath;

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BraviaTheatrePC");
        if (!Directory.Exists(appDataDir))
        {
            try { Directory.CreateDirectory(appDataDir); } catch { }
        }
        return Path.Combine(appDataDir, "settings.json");
    }

    public static AppSettings Load()
    {
        try
        {
            var path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch { }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var path = GetSettingsFilePath();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
