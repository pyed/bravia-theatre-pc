using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.UI.Models;

namespace BraviaTheatre.UI.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int HOTKEY_VOL_UP = 9001;
    private const int HOTKEY_VOL_DOWN = 9002;
    private const int HOTKEY_MUTE = 9003;
    private const int HOTKEY_SOUND_FIELD = 9004;
    private const int HOTKEY_VOICE_MODE = 9005;
    private const int HOTKEY_NIGHT_MODE = 9006;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly BraviaEngine _engine;
    private HwndSource? _hwndSource;
    private bool _isRegistered;

    public GlobalHotkeyService(BraviaEngine engine)
    {
        _engine = engine;
    }

    public void Register(AppSettings settings)
    {
        Unregister();

        if (!settings.EnableGlobalHotkeys) return;

        var parameters = new HwndSourceParameters("BraviaHotkeyMsgSink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(HwndHook);

        var hwnd = _hwndSource.Handle;

        RegisterSingle(hwnd, HOTKEY_VOL_UP, settings.HotkeyVolumeUp);
        RegisterSingle(hwnd, HOTKEY_VOL_DOWN, settings.HotkeyVolumeDown);
        RegisterSingle(hwnd, HOTKEY_MUTE, settings.HotkeyMute);
        RegisterSingle(hwnd, HOTKEY_SOUND_FIELD, settings.HotkeySoundField);
        RegisterSingle(hwnd, HOTKEY_VOICE_MODE, settings.HotkeyVoiceMode);
        RegisterSingle(hwnd, HOTKEY_NIGHT_MODE, settings.HotkeyNightMode);

        _isRegistered = true;
    }

    private static void RegisterSingle(IntPtr hwnd, int id, string hotkeyStr)
    {
        if (TryParseHotkey(hotkeyStr, out var mods, out var vk))
        {
            RegisterHotKey(hwnd, id, mods, vk);
        }
    }

    public static bool TryParseHotkey(string hotkeyStr, out uint modifiers, out uint vk)
    {
        modifiers = MOD_NOREPEAT;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkeyStr))
            return false;

        var parts = hotkeyStr.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        string keyPart = parts[^1];

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var mod = parts[i].ToLowerInvariant();
            if (mod is "ctrl" or "control") modifiers |= MOD_CONTROL;
            else if (mod is "alt") modifiers |= MOD_ALT;
            else if (mod is "shift") modifiers |= MOD_SHIFT;
            else if (mod is "win" or "windows") modifiers |= MOD_WIN;
        }

        vk = ParseKeyToVk(keyPart);
        return vk != 0;
    }

    private static uint ParseKeyToVk(string keyName)
    {
        keyName = keyName.Trim();
        if (Enum.TryParse<Key>(keyName, true, out var key))
        {
            int rawVk = KeyInterop.VirtualKeyFromKey(key);
            if (rawVk > 0) return (uint)rawVk;
        }

        return keyName.ToLowerInvariant() switch
        {
            "up" or "uparrow" => 0x26,
            "down" or "downarrow" => 0x28,
            "left" or "leftarrow" => 0x25,
            "right" or "rightarrow" => 0x27,
            "m" => 0x4D,
            "s" => 0x53,
            "v" => 0x56,
            "n" => 0x4E,
            "b" => 0x42,
            "p" => 0x50,
            _ => (uint)(keyName.Length == 1 ? char.ToUpperInvariant(keyName[0]) : 0)
        };
    }

    public void Unregister()
    {
        if (!_isRegistered || _hwndSource == null) return;

        var hwnd = _hwndSource.Handle;
        UnregisterHotKey(hwnd, HOTKEY_VOL_UP);
        UnregisterHotKey(hwnd, HOTKEY_VOL_DOWN);
        UnregisterHotKey(hwnd, HOTKEY_MUTE);
        UnregisterHotKey(hwnd, HOTKEY_SOUND_FIELD);
        UnregisterHotKey(hwnd, HOTKEY_VOICE_MODE);
        UnregisterHotKey(hwnd, HOTKEY_NIGHT_MODE);

        _hwndSource.RemoveHook(HwndHook);
        _hwndSource.Dispose();
        _hwndSource = null;
        _isRegistered = false;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            switch (id)
            {
                case HOTKEY_VOL_UP:
                    int newVolUp = Math.Clamp(_engine.CurrentState.Volume + 2, 0, 100);
                    _ = _engine.SetVolumeAsync(newVolUp);
                    handled = true;
                    break;
                case HOTKEY_VOL_DOWN:
                    int newVolDown = Math.Clamp(_engine.CurrentState.Volume - 2, 0, 100);
                    _ = _engine.SetVolumeAsync(newVolDown);
                    handled = true;
                    break;
                case HOTKEY_MUTE:
                    _ = _engine.ToggleMuteAsync();
                    handled = true;
                    break;
                case HOTKEY_SOUND_FIELD:
                    _ = _engine.ToggleSoundFieldAsync();
                    handled = true;
                    break;
                case HOTKEY_VOICE_MODE:
                    _ = _engine.ToggleVoiceModeAsync();
                    handled = true;
                    break;
                case HOTKEY_NIGHT_MODE:
                    _ = _engine.ToggleNightModeAsync();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
    }
}
