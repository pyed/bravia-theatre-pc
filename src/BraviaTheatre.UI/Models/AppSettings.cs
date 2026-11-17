using System;
using System.IO;
using System.Text;
using System.Text.Json;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.UI.Models;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Starting with Windows is not stored here: the registry Run entry is the only state that
    // matters, and a copy in this file drifted from it (see AutoStartService).
    public bool TrayIconGuidanceShown { get; set; }
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

    /// <summary>
    /// The network the soundbar was last found on ("192.168.1.0/24"). Discovery sweeps only this
    /// network for the soundbar; empty until it has been found once.
    /// </summary>
    public string? SoundbarNetwork { get; set; }
    public string LogLevel { get; set; } = "Critical";

    public static string SettingsFilePath => Path.Combine(App.GetAppDataDir(), "settings.json");

    public AppSettings Copy() => (AppSettings)MemberwiseClone();

    public static AppSettings Load(out string? warning) => Load(SettingsFilePath, out warning);

    internal static AppSettings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
            return new AppSettings();

        if (TryRead(path, out var loaded, out var error))
            return loaded!;

        // The defaults are saved over this file during the same start (the tray guidance marks
        // itself shown), which lost shortcuts, host and log level for good. Keep the unreadable
        // file beside it instead.
        var keptAs = path + ".bad";
        try
        {
            File.Move(path, keptAs, overwrite: true);
            warning = $"{error}\n\nDefault settings are in use. The unreadable file was kept as {keptAs}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"{error}\n\nDefault settings are in use.";
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
            var json = JsonSerializer.Serialize(this, WriteOptions);
            tempPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
            DurableFile.WriteNew(tempPath, Encoding.UTF8.GetBytes(json));
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

    private static bool TryRead(string path, out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
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
