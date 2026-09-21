using System.Windows.Input;
using BraviaTheatre.UI.Models;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

public sealed class HotkeyParsingTests
{
    // The Settings window renders Key.D0-Key.D9 as a bare digit, so the parser has to
    // resolve "5" as the digit. Enum.TryParse would otherwise read it as ordinal 5
    // (Key.Clear) and register a completely different physical key.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void DigitShortcutsBindTheDigitKey(int digit)
    {
        Assert.True(GlobalHotkeyService.TryParseHotkey($"Ctrl + Alt + {digit}", out _, out var virtualKey));
        Assert.Equal((uint)('0' + digit), virtualKey);
    }

    [Theory]
    [InlineData(Key.D0)]
    [InlineData(Key.D3)]
    [InlineData(Key.D5)]
    [InlineData(Key.D6)]
    [InlineData(Key.D9)]
    public void SettingsWindowDigitRenderingRoundTripsToTheSameKey(Key key)
    {
        // Mirrors SettingsWindow.HotkeyBox_PreviewKeyDown, which rewrites "D5" to "5".
        var rendered = key.ToString();
        if (rendered.StartsWith('D') && rendered.Length == 2 && char.IsAsciiDigit(rendered[1]))
            rendered = rendered[1].ToString();

        Assert.True(GlobalHotkeyService.TryParseHotkey($"Ctrl + Alt + {rendered}", out _, out var virtualKey));
        Assert.Equal((uint)KeyInterop.VirtualKeyFromKey(key), virtualKey);
    }

    [Theory]
    [InlineData("F5", 0x74u)]
    [InlineData("Up", 0x26u)]
    [InlineData("Down", 0x28u)]
    [InlineData("Left", 0x25u)]
    [InlineData("Right", 0x27u)]
    [InlineData("Space", 0x20u)]
    [InlineData("A", 0x41u)]
    [InlineData("m", 0x4Du)]
    [InlineData("OemPlus", 0xBBu)]
    public void NamedKeysKeepResolving(string keyName, uint expected)
    {
        Assert.True(GlobalHotkeyService.TryParseHotkey($"Ctrl + {keyName}", out _, out var virtualKey));
        Assert.Equal(expected, virtualKey);
    }

    [Theory]
    [InlineData("Ctrl + 12")]
    [InlineData("Ctrl + 999999")]
    [InlineData("Ctrl + -1")]
    [InlineData("Ctrl + 0x41")]
    public void MultiDigitAndNumericStringsAreRejectedRatherThanMisbound(string hotkey)
    {
        Assert.False(GlobalHotkeyService.TryParseHotkey(hotkey, out _, out var virtualKey));
        Assert.Equal(0u, virtualKey);
    }

    [Fact]
    public void DigitShortcutsValidateAsDistinctBindings()
    {
        var settings = new AppSettings
        {
            EnableGlobalHotkeys = true,
            HotkeyVolumeUp = "Ctrl + Alt + 1",
            HotkeyVolumeDown = "Ctrl + Alt + 2",
            HotkeyMute = "Ctrl + Alt + 3",
            HotkeySoundField = "Ctrl + Alt + 6",
            HotkeyVoiceMode = "Ctrl + Alt + 8",
            HotkeyNightMode = "Ctrl + Alt + 9"
        };

        Assert.True(GlobalHotkeyService.ValidateSettings(settings).Success);
    }

    // Before the fix these digits collided with real keys: 2 -> Backspace, 3 -> Tab,
    // 6 -> Enter, 8 -> Caps Lock, which silently swallowed those chords system-wide.
    [Theory]
    [InlineData("Ctrl + Alt + 2", "Ctrl + Alt + Back")]
    [InlineData("Ctrl + Alt + 3", "Ctrl + Alt + Tab")]
    [InlineData("Ctrl + Alt + 6", "Ctrl + Alt + Return")]
    [InlineData("Ctrl + Alt + 8", "Ctrl + Alt + Capital")]
    public void DigitShortcutsDoNotAliasOtherPhysicalKeys(string digitHotkey, string namedHotkey)
    {
        Assert.True(GlobalHotkeyService.TryParseHotkey(digitHotkey, out _, out var digitKey));
        Assert.True(GlobalHotkeyService.TryParseHotkey(namedHotkey, out _, out var namedKey));
        Assert.NotEqual(namedKey, digitKey);
    }
}
