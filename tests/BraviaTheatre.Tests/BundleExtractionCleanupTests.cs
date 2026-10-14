using BraviaTheatre.UI.Services;

namespace BraviaTheatre.Tests;

/// <summary>
/// Every build extracted its native libraries to a new folder under %TEMP%\.net and nothing
/// removed the old ones.
/// </summary>
public sealed class BundleExtractionCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BraviaTheatreTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FoldersLeftByOtherBuildsAreRemovedAndTheOneInUseIsKept()
    {
        var current = CreateBundle("BraviaTheatrePC", "current");
        var stale = CreateBundle("BraviaTheatrePC", "stale");
        var frameworkDependent = CreateBundle("BraviaTheatrePC-FrameworkDependent", "older");
        var unrelated = CreateBundle("SomeOtherApp", "bundle");

        var deleted = BundleExtractionCleanup.DeleteStaleFolders(
            _root,
            ["BraviaTheatrePC", "BraviaTheatrePC-FrameworkDependent", "braviatheatrepc"],
            [current + Path.DirectorySeparatorChar, Environment.SystemDirectory]);

        Assert.Equal(2, deleted);
        Assert.True(Directory.Exists(current));
        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(Path.GetDirectoryName(frameworkDependent)));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void FolderThatCannotBeDeletedIsLeftForALaterStart()
    {
        var locked = CreateBundle("BraviaTheatrePC", "locked");
        var stale = CreateBundle("BraviaTheatrePC", "stale");

        int deleted;
        using (File.Open(Path.Combine(locked, "wpfgfx_cor3.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            deleted = BundleExtractionCleanup.DeleteStaleFolders(_root, ["BraviaTheatrePC"], []);
        }

        Assert.Equal(1, deleted);
        Assert.True(Directory.Exists(locked));
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void NothingExtractedYetIsNotAnError()
    {
        Assert.Equal(0, BundleExtractionCleanup.DeleteStaleFolders(Path.Combine(_root, "absent"), ["BraviaTheatrePC"], []));
    }

    private string CreateBundle(string executableName, string bundleId)
    {
        var directory = Path.Combine(_root, executableName, bundleId);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "wpfgfx_cor3.dll"), [0x4D, 0x5A]);
        return directory;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
