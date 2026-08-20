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

    public static ImageSource? GetImageSource(string badgeKind)
    {
        if (ImageCache.TryGetValue(badgeKind, out var cached))
            return cached;

        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{badgeKind}.png", UriKind.Absolute);
            var bmp = new BitmapImage(uri);
            bmp.Freeze();
            ImageCache[badgeKind] = bmp;
            return bmp;
        }
        catch
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/Icons/idle.png", UriKind.Absolute);
                var bmp = new BitmapImage(uri);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    public static System.Drawing.Icon GetTrayIcon(string badgeKind)
    {
        if (IconCache.TryGetValue(badgeKind, out var cached))
            return cached;

        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{badgeKind}.png", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                using var mem = new MemoryStream();
                streamInfo.Stream.CopyTo(mem);
                mem.Position = 0;
                using var bmp = new System.Drawing.Bitmap(mem);
                var hIcon = bmp.GetHicon();
                var icon = System.Drawing.Icon.FromHandle(hIcon);
                IconCache[badgeKind] = icon;
                return icon;
            }
        }
        catch
        {
            // Fallback
        }

        try
        {
            var fallbackUri = new Uri("pack://application:,,,/Assets/Icons/idle.png", UriKind.Absolute);
            var fallbackStream = Application.GetResourceStream(fallbackUri);
            if (fallbackStream != null)
            {
                using var mem = new MemoryStream();
                fallbackStream.Stream.CopyTo(mem);
                mem.Position = 0;
                using var bmp = new System.Drawing.Bitmap(mem);
                var hIcon = bmp.GetHicon();
                var icon = System.Drawing.Icon.FromHandle(hIcon);
                return icon;
            }
        }
        catch
        {
            // Fallback
        }

        return System.Drawing.SystemIcons.Application;
    }
}
