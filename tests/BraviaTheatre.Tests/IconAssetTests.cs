using System.Collections;
using System.Resources;
using BraviaTheatre.UI.Services;
using Xunit;

namespace BraviaTheatre.Tests;

public class IconAssetTests
{
    private static readonly string[] RuntimeIconNames =
    [
        "atmos_truehd", "atmos", "truehd", "ddplus", "dd",
        "dtsx", "dtshd", "dts", "imax", "lpcm", "aac", "dsd",
        "360ra", "idle", "check"
    ];

    [Fact]
    public void EveryRuntimeIconIsEmbeddedInTheUiAssembly()
    {
        var assembly = typeof(IconHelper).Assembly;
        var generatedResourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(generatedResourceName)
            ?? throw new InvalidOperationException($"Missing {generatedResourceName}.");
        using var reader = new ResourceReader(stream);
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IDictionaryEnumerator enumerator = reader.GetEnumerator();
        while (enumerator.MoveNext())
            resources.Add((string)enumerator.Key);

        foreach (var iconName in RuntimeIconNames)
            Assert.Contains($"assets/icons/{iconName}.png", resources);
    }

    [Fact]
    public void NonAtmosDolbyAndDsdArtworkIsDistinct()
    {
        var iconDirectory = Path.Combine(GetRepositoryRoot(), "assets", "icons");
        var atmos = ReadIcon(iconDirectory, "atmos");

        Assert.False(atmos.SequenceEqual(ReadIcon(iconDirectory, "ddplus")));
        Assert.False(atmos.SequenceEqual(ReadIcon(iconDirectory, "dd")));
        Assert.False(atmos.SequenceEqual(ReadIcon(iconDirectory, "truehd")));
        Assert.False(ReadIcon(iconDirectory, "lpcm").SequenceEqual(ReadIcon(iconDirectory, "dsd")));
    }

    [Theory]
    [MemberData(nameof(RuntimeCodecIconNames))]
    public void RuntimeCodecIconSourceExists(string iconName)
    {
        var path = Path.Combine(GetRepositoryRoot(), "assets", "icons", $"{iconName}.png");
        Assert.True(File.Exists(path), $"Missing runtime icon source: {path}");
    }

    public static TheoryData<string> RuntimeCodecIconNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var iconName in RuntimeIconNames)
            {
                if (!string.Equals(iconName, "check", StringComparison.Ordinal))
                    data.Add(iconName);
            }
            return data;
        }
    }

    private static byte[] ReadIcon(string directory, string name) =>
        File.ReadAllBytes(Path.Combine(directory, $"{name}.png"));

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
