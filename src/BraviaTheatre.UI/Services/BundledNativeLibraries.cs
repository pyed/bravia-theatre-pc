using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BraviaTheatre.UI.Services;

/// <summary>
/// Loads the native libraries that WPF and WebView2 would otherwise load on demand, and keeps
/// them loaded for the life of the process.
/// </summary>
/// <remarks>
/// A single-file publish extracts its native libraries to %TEMP%\.net\BraviaTheatrePC when the
/// app starts. Windows temp cleanup deletes files there that have sat unused for a week, and a
/// tray app outlives that. WPF loads its pen and touch library only when a digitizer appears,
/// such as the virtual touch screen of a Remote Desktop session from a tablet, so once the file
/// was gone every later device change threw DllNotFoundException. A loaded library cannot be
/// deleted, and the host restores missing files on the next start. The handles are never freed.
/// </remarks>
internal static class BundledNativeLibraries
{
    internal const string PenImc = "PenImc_cor3.dll";

    internal const string DisableStylusAndTouchSupportSwitch =
        "Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport";

    // Each library is resolved through the assembly that imports it, so it is found exactly
    // where that assembly's DllImports look. wpfgfx_cor3.dll keeps D3DCompiler_47_cor3.dll,
    // its own import, loaded with it.
    internal static IReadOnlyList<(string Library, string Importer)> Libraries { get; } =
    [
        (PenImc, "PresentationCore"),
        ("PresentationNative_cor3.dll", "PresentationCore"),
        ("wpfgfx_cor3.dll", "PresentationCore"),
        // WPF's C++/CLI text layer calls into this from its runtime teardown, so without it
        // the app crashed on the way out.
        ("vcruntime140_cor3.dll", "DirectWriteForwarder"),
        ("WebView2Loader.dll", "Microsoft.Web.WebView2.Core")
    ];

    /// <summary>Loads every library and returns the ones that could not be loaded.</summary>
    internal static IReadOnlyList<string> Pin() =>
        Pin(Libraries, static (library, importer) => NativeLibrary.TryLoad(library, importer, null, out _));

    internal static IReadOnlyList<string> Pin(
        IEnumerable<(string Library, string Importer)> libraries,
        Func<string, Assembly, bool> tryLoad)
    {
        var missing = new List<string>();
        foreach (var (library, importer) in libraries)
        {
            bool loaded;
            try
            {
                loaded = tryLoad(library, Assembly.Load(importer));
            }
            catch (Exception)
            {
                // This is a safeguard; it must never be the reason startup fails.
                loaded = false;
            }

            if (!loaded) missing.Add(library);
        }

        return missing;
    }
}
