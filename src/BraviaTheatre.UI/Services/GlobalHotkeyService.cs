using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using BraviaTheatre.Core.Engine;

namespace BraviaTheatre.UI.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int VK_UP = 0x26;
    private const int VK_DOWN = 0x28;
    private const int VK_M = 0x4D;
    private const int VK_S = 0x53;
    private const int VK_V = 0x56;
    private const int VK_N = 0x4E;

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

    public void Register()
    {
        if (_isRegistered) return;

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

        uint mods = MOD_WIN | MOD_ALT | MOD_NOREPEAT;

        RegisterHotKey(hwnd, HOTKEY_VOL_UP, mods, VK_UP);
        RegisterHotKey(hwnd, HOTKEY_VOL_DOWN, mods, VK_DOWN);
        RegisterHotKey(hwnd, HOTKEY_MUTE, mods, VK_M);
        RegisterHotKey(hwnd, HOTKEY_SOUND_FIELD, mods, VK_S);
        RegisterHotKey(hwnd, HOTKEY_VOICE_MODE, mods, VK_V);
        RegisterHotKey(hwnd, HOTKEY_NIGHT_MODE, mods, VK_N);

        _isRegistered = true;
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
