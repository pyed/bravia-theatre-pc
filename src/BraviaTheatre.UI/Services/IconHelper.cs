using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BraviaTheatre.UI.Services;

public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource> ImageCache = new();
    private static readonly ConcurrentDictionary<string, System.Drawing.Icon> IconCache = new();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? GetImageSource(string badgeKind)
    {
        var key = NormalizeBadgeKind(badgeKind);
        if (ImageCache.TryGetValue(key, out var cached)) return cached;

        var image = TryLoadImage(key) ?? (key == "idle" ? null : TryLoadImage("idle"));
        if (image != null) ImageCache.TryAdd(key, image);
        return image;
    }

    public static System.Drawing.Icon GetTrayIcon(string badgeKind)
    {
        var key = NormalizeBadgeKind(badgeKind);
        if (IconCache.TryGetValue(key, out var cached)) return cached;

        var created = TryCreateOwnedIcon(key) ??
                      (key == "idle" ? null : TryCreateOwnedIcon("idle")) ??
                      (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        var winner = IconCache.GetOrAdd(key, created);
        if (!ReferenceEquals(winner, created)) created.Dispose();
        return winner;
    }

    public static void DisposeCachedIcons()
    {
        foreach (var entry in IconCache)
            entry.Value.Dispose();
        IconCache.Clear();
        ImageCache.Clear();
    }

    private static ImageSource? TryLoadImage(string key)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{key}.png", UriKind.Absolute);
            var bitmap = new BitmapImage(uri);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static System.Drawing.Icon? TryCreateOwnedIcon(string key)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{key}.png", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo == null) return null;

            using var resourceStream = streamInfo.Stream;
            using var memory = new MemoryStream();
            resourceStream.CopyTo(memory);
            memory.Position = 0;
            using var bitmap = new System.Drawing.Bitmap(memory);
            var nativeHandle = bitmap.GetHicon();
            try
            {
                var borrowed = System.Drawing.Icon.FromHandle(nativeHandle);
                return (System.Drawing.Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(nativeHandle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeBadgeKind(string? badgeKind) =>
        string.IsNullOrWhiteSpace(badgeKind) || badgeKind.Equals("standby", StringComparison.OrdinalIgnoreCase)
            ? "idle"
            : badgeKind.Trim().ToLowerInvariant();
}
