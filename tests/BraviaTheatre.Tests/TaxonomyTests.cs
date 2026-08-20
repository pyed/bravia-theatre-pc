using BraviaTheatre.Core.Models;
using Xunit;

namespace BraviaTheatre.Tests;

public class TaxonomyTests
{
    [Theory]
    [InlineData("dolby_atmos_truehd", "atmos_truehd")]
    [InlineData("dolby_atmos_mat", "atmos")]
    [InlineData("dolby_atmos_digital_plus", "atmos")]
    [InlineData("dolby_digital_truehd", "truehd")]
    [InlineData("dolby_digital_plus", "ddplus")]
    [InlineData("dolby_mat", "ddplus")]
    [InlineData("dolby_digital", "dd")]
    [InlineData("dts_x", "dtsx")]
    [InlineData("dts_x_master_audio", "dtsx")]
    [InlineData("dts_x_imax", "imax")]
    [InlineData("dts_hd_master_audio", "dtshd")]
    [InlineData("dts", "dts")]
    [InlineData("lpcm", "lpcm")]
    [InlineData("linear_pcm", "lpcm")]
    [InlineData("360_reality_audio", "360ra")]
    [InlineData("aac", "aac")]
    public void TestCodecClassification(string input, string expectedKind)
    {
        var actual = CodecTaxonomy.Classify(input);
        Assert.Equal(expectedKind, actual);
    }

    [Theory]
    [InlineData("dolby_atmos_truehd", null, "Dolby Atmos (TrueHD)")]
    [InlineData("lpcm", "7.1", "LPCM (7.1 ch)")]
    [InlineData("dts_x", "5.1.2", "DTS:X (5.1.2 ch)")]
    public void TestHumanReadableFormatting(string codec, string? channel, string expected)
    {
        var actual = CodecTaxonomy.FormatHumanReadable(codec, channel);
        Assert.Equal(expected, actual);
    }
}
