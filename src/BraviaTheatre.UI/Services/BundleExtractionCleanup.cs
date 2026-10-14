using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BraviaTheatre.UI.Services;

/// <summary>
/// Removes the native-library folders that other builds of the app extracted to %TEMP%\.net.
/// </summary>
/// <remarks>
/// The single-file host extracts to %TEMP%\.net\&lt;executable name&gt;\&lt;bundle id&gt;, a new folder for
/// every build, and never removes the old ones: the reporter's PC had a dozen. The folder this
/// process loaded its libraries from is kept, and the host restores it if it ever goes missing.
/// </remarks>
internal static class BundleExtractionCleanup
{
    // The release names the framework-dependent executable differently, so its folder is too.
    private static readonly string[] ReleaseExecutableNames =
        ["BraviaTheatrePC", "BraviaTheatrePC-FrameworkDependent"];

    /// <summary>Deletes stale folders and returns how many were removed.</summary>
    internal static int DeleteStaleFolders()
    {
        var baseDirectory = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), ".net");

        var executableNames = ReleaseExecutableNames.ToList();
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is { Length: > 0 } current)
            executableNames.Add(current);

        return DeleteStaleFolders(baseDirectory, executableNames, GetDirectoriesInUse());
    }

    internal static int DeleteStaleFolders(
        string baseDirectory,
        IEnumerable<string> executableNames,
        IEnumerable<string> directoriesInUse)
    {
        var keep = new HashSet<string>(directoriesInUse.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        foreach (var name in executableNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var appDirectory = Path.Combine(baseDirectory, name);
            if (!Directory.Exists(appDirectory)) continue;

            foreach (var bundleDirectory in Directory.EnumerateDirectories(appDirectory))
            {
                if (keep.Contains(Normalize(bundleDirectory))) continue;
                try
                {
                    Directory.Delete(bundleDirectory, recursive: true);
                    deleted++;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Still in use by another copy of the app; a later start removes it.
                }
            }

            try
            {
                // Only succeeds once nothing is left in it, as for the other variant's folder.
                Directory.Delete(appDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    // The host lists the extraction folder among the native search directories, and every
    // library this process loaded from there marks it too.
    private static IEnumerable<string> GetDirectoriesInUse()
    {
        var directories = new List<string>();
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirectories)
            directories.AddRange(searchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            using (module)
            {
                if (Path.GetDirectoryName(module.FileName) is { Length: > 0 } directory)
                    directories.Add(directory);
            }
        }

        return directories;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
