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
    public string? StaticHost { get; set; } = "";
    public int StaticPort { get; set; } = 55051;

    private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
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
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
