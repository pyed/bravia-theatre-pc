using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using BraviaTheatre.Core.Engine;
using BraviaTheatre.UI.Models;

namespace BraviaTheatre.UI.Services;

public sealed record HotkeyOperationResult(bool Success, string Message, bool PreviousBindingsRestored = false);

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
    private readonly HashSet<int> _registeredIds = new();
    private HwndSource? _hwndSource;
    private AppSettings? _registeredSettings;

    public GlobalHotkeyService(BraviaEngine engine)
    {
        _engine = engine;
    }

    public static HotkeyOperationResult ValidateSettings(AppSettings settings)
    {
        if (!settings.EnableGlobalHotkeys)
            return new HotkeyOperationResult(true, "Global hotkeys are disabled.");

        var seen = new Dictionary<(uint Modifiers, uint Key), string>();
        foreach (var binding in GetBindings(settings))
        {
            if (string.IsNullOrWhiteSpace(binding.Value)) continue;
            if (!TryParseHotkey(binding.Value, out var modifiers, out var key))
            {
                return new HotkeyOperationResult(false,
                    $"{binding.Name} is not a valid global shortcut. Use at least one modifier (Ctrl, Alt, Shift, or Win) plus one key, or clear it to disable that shortcut.");
            }

            var normalized = (modifiers & ~MOD_NOREPEAT, key);
            if (seen.TryGetValue(normalized, out var existing))
            {
                return new HotkeyOperationResult(false,
                    $"{binding.Name} duplicates {existing}. Each global shortcut must be unique.");
            }
            seen[normalized] = binding.Name;
        }

        return new HotkeyOperationResult(true, "Global hotkeys are valid.");
    }

    public HotkeyOperationResult Register(AppSettings settings)
    {
        var validation = ValidateSettings(settings);
        if (!validation.Success) return validation;

        var previous = _registeredSettings;
        UnregisterCore(clearSnapshot: false);

        if (!settings.EnableGlobalHotkeys)
        {
            _registeredSettings = null;
            return new HotkeyOperationResult(true, "Global hotkeys are disabled.");
        }

        var result = RegisterCore(settings);
        if (result.Success)
        {
            _registeredSettings = CloneSettings(settings);
            return result;
        }

        UnregisterCore(clearSnapshot: false);
        var restored = previous != null && RegisterCore(previous).Success;
        if (!restored) UnregisterCore(clearSnapshot: false);
        _registeredSettings = restored ? previous : null;
        return result with { PreviousBindingsRestored = restored };
    }

    private HotkeyOperationResult RegisterCore(AppSettings settings)
    {
        try
        {
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

            foreach (var binding in GetBindings(settings))
            {
                if (string.IsNullOrWhiteSpace(binding.Value)) continue;
                TryParseHotkey(binding.Value, out var modifiers, out var key);
                if (!RegisterHotKey(hwnd, binding.Id, modifiers, key))
                {
                    var error = Marshal.GetLastWin32Error();
                    return new HotkeyOperationResult(false,
                        $"Windows could not register {binding.Name} ({binding.Value}). It may already be used by another app. Win32 error: {error}.");
                }
                _registeredIds.Add(binding.Id);
            }

            return new HotkeyOperationResult(true, "Global hotkeys registered.");
        }
        catch (Exception ex)
        {
            return new HotkeyOperationResult(false, $"Could not initialize global hotkeys: {ex.Message}");
        }
    }

    public static bool TryParseHotkey(string hotkeyStr, out uint modifiers, out uint vk)
    {
        modifiers = MOD_NOREPEAT;
        vk = 0;
        if (string.IsNullOrWhiteSpace(hotkeyStr)) return false;

        var parts = hotkeyStr.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var actualModifiers = 0u;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var flag = parts[index].ToLowerInvariant() switch
            {
                "ctrl" or "control" => MOD_CONTROL,
                "alt" => MOD_ALT,
                "shift" => MOD_SHIFT,
                "win" or "windows" => MOD_WIN,
                _ => 0u
            };
            if (flag == 0 || (actualModifiers & flag) != 0) return false;
            actualModifiers |= flag;
        }
        if (actualModifiers == 0) return false;

        vk = ParseKeyToVk(parts[^1]);
        if (vk == 0) return false;
        modifiers |= actualModifiers;
        return true;
    }

    private static uint ParseKeyToVk(string keyName)
    {
        keyName = keyName.Trim();
        if (Enum.TryParse<Key>(keyName, true, out var key))
        {
            var rawVk = KeyInterop.VirtualKeyFromKey(key);
            if (rawVk > 0) return (uint)rawVk;
        }

        return keyName.ToLowerInvariant() switch
        {
            "up" or "uparrow" => 0x26,
            "down" or "downarrow" => 0x28,
            "left" or "leftarrow" => 0x25,
            "right" or "rightarrow" => 0x27,
            _ => (uint)(keyName.Length == 1 ? char.ToUpperInvariant(keyName[0]) : 0)
        };
    }

    public void Unregister() => UnregisterCore(clearSnapshot: true);

    private void UnregisterCore(bool clearSnapshot)
    {
        if (_hwndSource != null)
        {
            var hwnd = _hwndSource.Handle;
            foreach (var id in _registeredIds)
                UnregisterHotKey(hwnd, id);
            _registeredIds.Clear();
            _hwndSource.RemoveHook(HwndHook);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
        if (clearSnapshot) _registeredSettings = null;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        handled = true;
        switch (wParam.ToInt32())
        {
            case HOTKEY_VOL_UP:
                _ = _engine.SetVolumeAsync(Math.Clamp(_engine.CurrentState.Volume + 2, 0, 100));
                break;
            case HOTKEY_VOL_DOWN:
                _ = _engine.SetVolumeAsync(Math.Clamp(_engine.CurrentState.Volume - 2, 0, 100));
                break;
            case HOTKEY_MUTE:
                _ = _engine.ToggleMuteAsync();
                break;
            case HOTKEY_SOUND_FIELD:
                _ = _engine.ToggleSoundFieldAsync();
                break;
            case HOTKEY_VOICE_MODE:
                _ = _engine.ToggleVoiceModeAsync();
                break;
            case HOTKEY_NIGHT_MODE:
                _ = _engine.ToggleNightModeAsync();
                break;
            default:
                handled = false;
                break;
        }
        return IntPtr.Zero;
    }

    private static IReadOnlyList<(int Id, string Name, string Value)> GetBindings(AppSettings settings) =>
        new[]
        {
            (HOTKEY_VOL_UP, "Volume Up", settings.HotkeyVolumeUp),
            (HOTKEY_VOL_DOWN, "Volume Down", settings.HotkeyVolumeDown),
            (HOTKEY_MUTE, "Mute", settings.HotkeyMute),
            (HOTKEY_SOUND_FIELD, "Sound Field", settings.HotkeySoundField),
            (HOTKEY_VOICE_MODE, "Voice Mode", settings.HotkeyVoiceMode),
            (HOTKEY_NIGHT_MODE, "Night Mode", settings.HotkeyNightMode)
        };

    private static AppSettings CloneSettings(AppSettings settings) => new()
    {
        EnableGlobalHotkeys = settings.EnableGlobalHotkeys,
        HotkeyVolumeUp = settings.HotkeyVolumeUp,
        HotkeyVolumeDown = settings.HotkeyVolumeDown,
        HotkeyMute = settings.HotkeyMute,
        HotkeySoundField = settings.HotkeySoundField,
        HotkeyVoiceMode = settings.HotkeyVoiceMode,
        HotkeyNightMode = settings.HotkeyNightMode
    };

    public void Dispose() => Unregister();
}
