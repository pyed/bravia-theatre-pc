using System.Text.RegularExpressions;
using Xunit;

namespace BraviaTheatre.Tests;

/// <summary>
/// Guards the publish settings that keep the tray app's resident footprint small.
/// Compressing the single-file bundle makes the runtime decompress every managed
/// assembly into private memory on load instead of mapping it from the file, which
/// measured 89 MB of private working set at idle against 23 MB uncompressed.
/// </summary>
public class PublishFootprintTests
{
    private static readonly string[] PublishInstructions =
    [
        Path.Combine(".github", "workflows", "ci.yml"),
        Path.Combine(".github", "workflows", "release.yml"),
        "README.md"
    ];

    [Fact]
    public void SingleFileBundleStaysUncompressedSoAssembliesAreMappedNotCopied()
    {
        var project = ReadRepositoryFile(Path.Combine("src", "BraviaTheatre.UI", "BraviaTheatre.UI.csproj"));

        Assert.Contains("<EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationIsPrecompiledSoStartupDoesNotJitTheWholeUi()
    {
        var project = ReadRepositoryFile(Path.Combine("src", "BraviaTheatre.UI", "BraviaTheatre.UI.csproj"));

        Assert.Contains("<PublishReadyToRun>true</PublishReadyToRun>", project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NoPublishCommandReenablesBundleCompressionOnTheCommandLine(int instructionIndex)
    {
        // A command-line -p: switch silently overrides the project setting, so the
        // guard above is only meaningful while the workflows and documented publish
        // command leave it alone.
        var instructions = ReadRepositoryFile(PublishInstructions[instructionIndex]);

        Assert.DoesNotContain("EnableCompressionInSingleFile", instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleasePublishesTheStandaloneExecutableAsAZipSoTheDownloadStaysSmall()
    {
        var release = ReadRepositoryFile(Path.Combine(".github", "workflows", "release.yml")).ReplaceLineEndings("\n");

        Assert.Contains("publish/BraviaTheatrePC.zip", release, StringComparison.Ordinal);
        Assert.Contains("Compress-Archive", release, StringComparison.Ordinal);

        // The uncompressed executable is far too large to upload on its own.
        var assets = Regex.Match(release, @"files:[ \t]*\|\n(?<body>(?:[ \t]+\S.*\n?)+)").Groups["body"].Value;
        Assert.NotEmpty(assets);
        var uploaded = assets.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim());
        Assert.DoesNotContain("publish/BraviaTheatrePC.exe", uploaded);
        Assert.Contains("publish/BraviaTheatrePC.zip", uploaded);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(GetRepositoryRoot(), relativePath));

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BraviaTheatrePC.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}
