using System;
using System.Globalization;
using System.IO;

namespace BraviaTheatre.UI.Services;

internal static class DailyLogFile
{
    internal const int RetentionDays = 14;
    private const string FilePrefix = "BraviaTheatrePC-";
    private const string FileExtension = ".log";

    internal static string GetDirectory(string appDataDirectory) =>
        Path.Combine(appDataDirectory, "Logs");

    internal static string GetPath(string appDataDirectory, DateTime localTime) =>
        Path.Combine(
            GetDirectory(appDataDirectory),
            $"{FilePrefix}{localTime:yyyy-MM-dd}{FileExtension}");

    internal static void AppendLine(string appDataDirectory, DateTime localTime, string line)
    {
        var directory = GetDirectory(appDataDirectory);
        Directory.CreateDirectory(directory);

        using var stream = new FileStream(
            GetPath(appDataDirectory, localTime),
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
    }

    internal static void DeleteExpiredFiles(string appDataDirectory, DateTime localNow)
    {
        var directory = GetDirectory(appDataDirectory);
        if (!Directory.Exists(directory)) return;

        var oldestDateToKeep = localNow.Date.AddDays(-(RetentionDays - 1));
        foreach (var path in Directory.EnumerateFiles(directory, $"{FilePrefix}*{FileExtension}"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dateText = name.Length > FilePrefix.Length
                ? name[FilePrefix.Length..]
                : string.Empty;

            if (DateTime.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var fileDate)
                && fileDate.Date < oldestDateToKeep)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
