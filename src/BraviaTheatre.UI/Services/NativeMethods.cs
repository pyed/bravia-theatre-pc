using System;
using System.Runtime.InteropServices;

namespace BraviaTheatre.UI.Services;

/// <summary>
/// Win32 calls used by more than one window or service; single-use declarations stay with their
/// caller. Every import in this assembly resolves from System32 only (see AssemblyInfo).
/// </summary>
internal static partial class NativeMethods
{
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative WorkArea;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out PointNative point);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromPoint(PointNative point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hwnd, out RectNative rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageTime();

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint RegisterWindowMessage(string message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
