using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Windows;
using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

/// <summary>
/// A single-file publish extracts native libraries to the temp directory, where Windows deletes
/// the ones that are not loaded. WPF loads its pen and touch library only when a digitizer first
/// appears, so a Remote Desktop session from a tablet days after startup found it deleted and
/// every device change raised an error dialog.
/// </summary>
public class NativeLibraryPinningTests
{
    [Fact]
    public void EveryNativeLibraryWpfShipsIsPinned()
    {
        // WPF ships its native libraries beside its assemblies, and a single-file publish
        // bundles exactly those. Imports that resolve elsewhere, like user32, are not ours.
        var directory = Path.GetDirectoryName(typeof(UIElement).Assembly.Location)!;
        var shipped = Directory.EnumerateFiles(directory, "*.dll")
            .SelectMany(ReadNativeImports)
            .Where(library => File.Exists(Path.Combine(directory, library)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Contains(BundledNativeLibraries.PenImc, shipped, StringComparer.OrdinalIgnoreCase);
        foreach (var library in shipped)
        {
            Assert.Contains(BundledNativeLibraries.Libraries,
                pinned => pinned.Library.Equals(library, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void EachLibraryIsResolvedThroughAnAssemblyThatImportsIt()
    {
        // Resolving through the importer is what makes the pinned file the one its
        // DllImports bind to, rather than another copy elsewhere on the probing path.
        foreach (var (library, importer) in BundledNativeLibraries.Libraries)
        {
            Assert.Contains(ReadNativeImports(Assembly.Load(importer).Location),
                imported => imported.Equals(library, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void EveryLibraryLoads()
    {
        Assert.Empty(BundledNativeLibraries.Pin());
    }

    [Fact]
    public void LibrariesThatFailToLoadAreReportedWithoutStoppingTheRest()
    {
        var importer = typeof(NativeLibraryPinningTests).Assembly.GetName().Name!;
        var attempted = new List<string>();

        var missing = BundledNativeLibraries.Pin(
            [
                ("absent.dll", importer),
                ("broken.dll", importer),
                ("orphan.dll", "No.Such.Assembly"),
                ("present.dll", importer)
            ],
            (library, _) =>
            {
                attempted.Add(library);
                return library switch
                {
                    "absent.dll" => false,
                    "broken.dll" => throw new BadImageFormatException(),
                    _ => true
                };
            });

        Assert.Equal(["absent.dll", "broken.dll", "orphan.dll"], missing);
        Assert.Equal(["absent.dll", "broken.dll", "present.dll"], attempted);
    }

    private static string[] ReadNativeImports(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var portableExecutable = new PEReader(stream);
        if (!portableExecutable.HasMetadata) return [];

        var metadata = portableExecutable.GetMetadataReader();
        return Enumerable.Range(1, metadata.GetTableRowCount(TableIndex.ModuleRef))
            .Select(row => metadata.GetString(
                metadata.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name))
            .ToArray();
    }
}
