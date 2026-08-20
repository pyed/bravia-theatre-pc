using System;
using System.Collections.Generic;

namespace BraviaTheatre.Core.Models;

public static class CodecTaxonomy
{
    private static readonly Dictionary<string, string> CodecMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dolby Atmos
        ["dolby_atmos_truehd"] = "atmos_truehd",
        ["dolby_atmos_mat"] = "atmos",
        ["dolby_atmos_digital_plus"] = "atmos",

        // Dolby Family
        ["dolby_digital_truehd"] = "truehd",
        ["dolby_digital_plus"] = "ddplus",
        ["dolby_mat"] = "ddplus",
        ["dolby_digital"] = "dd",

        // DTS Family
        ["dts_x"] = "dtsx",
        ["dts_x_master_audio"] = "dtsx",
        ["dts_x_high_resolution"] = "dtsx",
        ["dts_x_imax"] = "imax",
        ["dts_hd_master_audio"] = "dtshd",
        ["dts_hd_high_resolution"] = "dtshd",
        ["dts_hd"] = "dtshd",
        ["dts_express"] = "dts",
        ["dts_96_24"] = "dts",
        ["dts"] = "dts",

        // Linear PCM
        ["lpcm"] = "lpcm",
        ["pcm"] = "lpcm",
        ["linear_pcm"] = "lpcm",

        // Spatial & Stereo
        ["360_reality_audio"] = "360ra",
        ["360ra"] = "360ra",
        ["aac"] = "aac",
        ["dsd"] = "lpcm"
    };

    private static readonly Dictionary<string, string> HumanMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dolby_atmos_truehd"] = "Dolby Atmos (TrueHD)",
        ["dolby_atmos_mat"] = "Dolby Atmos (MAT)",
        ["dolby_atmos_digital_plus"] = "Dolby Atmos (DD+)",
        ["dolby_digital_truehd"] = "Dolby TrueHD",
        ["dolby_digital_plus"] = "Dolby Digital Plus",
        ["dolby_mat"] = "Dolby MAT",
        ["dolby_digital"] = "Dolby Digital",
        ["dts_x"] = "DTS:X",
        ["dts_x_master_audio"] = "DTS:X Master Audio",
        ["dts_x_high_resolution"] = "DTS:X High Resolution",
        ["dts_x_imax"] = "IMAX Enhanced DTS:X",
        ["dts_hd_master_audio"] = "DTS-HD MA",
        ["dts_hd_high_resolution"] = "DTS-HD HRA",
        ["dts_hd"] = "DTS-HD",
        ["dts_express"] = "DTS Express",
        ["dts_96_24"] = "DTS 96/24",
        ["dts"] = "DTS Digital Surround",
        ["lpcm"] = "LPCM",
        ["pcm"] = "PCM",
        ["linear_pcm"] = "Linear PCM",
        ["360_reality_audio"] = "360 Reality Audio",
        ["360ra"] = "360 Reality Audio",
        ["aac"] = "AAC",
        ["dsd"] = "DSD",
        ["idle"] = "Idle",
        ["standby"] = "Standby"
    };

    public static string Classify(string? rawCodec)
    {
        if (string.IsNullOrWhiteSpace(rawCodec))
            return "idle";

        var clean = rawCodec.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

        if (CodecMap.TryGetValue(clean, out var kind))
            return kind;

        if (clean.Contains("atmos")) return "atmos";
        if (clean.Contains("truehd")) return "truehd";
        if (clean.Contains("dts_x") || clean.Contains("dtsx")) return "dtsx";
        if (clean.Contains("dts_hd") || clean.Contains("dtshd")) return "dtshd";
        if (clean.Contains("dts")) return "dts";
        if (clean.Contains("pcm")) return "lpcm";
        if (clean.Contains("dolby")) return "dd";
        if (clean.Contains("360")) return "360ra";
        if (clean.Contains("aac")) return "aac";

        return "idle";
    }

    public static string FormatHumanReadable(string? rawCodec, string? channel = null)
    {
        if (string.IsNullOrWhiteSpace(rawCodec))
            return "Idle";

        var clean = rawCodec.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        string name = HumanMap.TryGetValue(clean, out var label) ? label : rawCodec.ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(channel))
        {
            var ch = channel.Trim();
            if (!ch.EndsWith("ch", StringComparison.OrdinalIgnoreCase))
                ch += " ch";
            return $"{name} ({ch})";
        }

        return name;
    }
}
