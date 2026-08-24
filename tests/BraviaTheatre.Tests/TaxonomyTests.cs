using BraviaTheatre.Core.Models;
using Xunit;

namespace BraviaTheatre.Tests;

public class TaxonomyTests
{
    [Theory]
    [InlineData("dolby_atmos_truehd", "atmos_truehd", "Dolby Atmos (TrueHD)")]
    [InlineData("dolby_atmos_mat", "atmos", "Dolby Atmos (MAT)")]
    [InlineData("dolby_atmos_digital_plus", "atmos", "Dolby Atmos (DD+)")]
    [InlineData("dolby_atmos", "atmos", "Dolby Atmos")]
    [InlineData("atmos", "atmos", "Dolby Atmos")]
    [InlineData("dolby_digital_truehd", "truehd", "Dolby TrueHD")]
    [InlineData("dolby_digital_plus", "ddplus", "Dolby Digital Plus")]
    [InlineData("dolby_mat", "ddplus", "Dolby MAT")]
    [InlineData("dolby_audio", "ddplus", "Dolby Audio")]
    [InlineData("ddplus", "ddplus", "Dolby Digital Plus")]
    [InlineData("DD+", "ddplus", "Dolby Digital Plus")]
    [InlineData("eac3", "ddplus", "Dolby Digital Plus")]
    [InlineData("e-ac3", "ddplus", "Dolby Digital Plus")]
    [InlineData("E-AC-3", "ddplus", "Dolby Digital Plus")]
    [InlineData("dolby_digital", "dd", "Dolby Digital")]
    [InlineData("dd", "dd", "Dolby Digital")]
    [InlineData("ac3", "dd", "Dolby Digital")]
    [InlineData("AC-3", "dd", "Dolby Digital")]
    [InlineData("dts_x", "dtsx", "DTS:X")]
    [InlineData("DTS:X", "dtsx", "DTS:X")]
    [InlineData("dts_x_master_audio", "dtsx", "DTS:X Master Audio")]
    [InlineData("dts_x_high_resolution", "dtsx", "DTS:X High Resolution")]
    [InlineData("dts_x_imax", "imax", "IMAX Enhanced DTS:X")]
    [InlineData("imax_dts_x", "imax", "IMAX Enhanced DTS:X")]
    [InlineData("imax_dts", "imax", "IMAX Enhanced DTS")]
    [InlineData("dts_hd_master_audio", "dtshd", "DTS-HD MA")]
    [InlineData("dts_hd_high_resolution", "dtshd", "DTS-HD HRA")]
    [InlineData("dts_hd", "dtshd", "DTS-HD")]
    [InlineData("dtshd", "dtshd", "DTS-HD")]
    [InlineData("dts_es_6.1_matrix", "dts", "DTS-ES Matrix 6.1")]
    [InlineData("dts_es_6.1_discrete", "dts", "DTS-ES Discrete 6.1")]
    [InlineData("dts_es_8ch_discrete", "dts", "DTS-ES Discrete 8ch")]
    [InlineData("dts_96_24", "dts", "DTS 96/24")]
    [InlineData("dts_express", "dts", "DTS Express")]
    [InlineData("dts_unknown", "dts", "DTS Digital Surround")]
    [InlineData("dts", "dts", "DTS Digital Surround")]
    [InlineData("lpcm", "lpcm", "LPCM")]
    [InlineData("pcm", "lpcm", "PCM")]
    [InlineData("linear_pcm", "lpcm", "Linear PCM")]
    [InlineData("multichannel_pcm", "lpcm", "Multichannel PCM")]
    [InlineData("mpeg-2_aac", "aac", "MPEG-2 AAC")]
    [InlineData("mpeg-4_aac", "aac", "MPEG-4 AAC")]
    [InlineData("aac", "aac", "AAC")]
    [InlineData("dsd", "dsd", "DSD")]
    [InlineData("360_reality_audio", "360ra", "360 Reality Audio")]
    [InlineData("360ra", "360ra", "360 Reality Audio")]
    [InlineData("imax_off", "idle", "IMAX Off")]
    [InlineData("none", "idle", "No Audio")]
    [InlineData("unknown", "idle", "Unknown")]
    public void KnownCodecsHaveTheExpectedBadgeAndLabel(string raw, string badge, string label)
    {
        Assert.Equal(badge, CodecTaxonomy.Classify(raw));
        Assert.Equal(label, CodecTaxonomy.FormatHumanReadable(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingCodecUsesTheIdleBadge(string? raw)
    {
        Assert.Equal("idle", CodecTaxonomy.Classify(raw));
        Assert.Equal("Idle", CodecTaxonomy.FormatHumanReadable(raw));
    }

    [Fact]
    public void UnrecognizedCodecDoesNotClaimAFormatBadge()
    {
        Assert.Equal("idle", CodecTaxonomy.Classify("some_future_codec"));
        Assert.Equal("SOME_FUTURE_CODEC", CodecTaxonomy.FormatHumanReadable("some_future_codec"));
    }

    [Theory]
    [InlineData("dolby_digital_plus", "2.0", "Dolby Digital Plus (2.0 ch)")]
    [InlineData("lpcm", "7.1 ch", "LPCM (7.1 ch)")]
    [InlineData("dts_x", "5.1.2", "DTS:X (5.1.2 ch)")]
    public void ChannelFormattingIsConsistent(string codec, string channel, string expected)
    {
        Assert.Equal(expected, CodecTaxonomy.FormatHumanReadable(codec, channel));
    }
}
