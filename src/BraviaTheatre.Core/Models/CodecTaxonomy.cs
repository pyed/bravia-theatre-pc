using System;
using System.Collections.Generic;
using System.Text;

namespace BraviaTheatre.Core.Models;

public static class CodecTaxonomy
{
    private readonly record struct CodecInfo(string BadgeKind, string DisplayName);

    private static readonly Dictionary<string, CodecInfo> CodecMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dolby Atmos
        ["dolby_atmos_truehd"] = new("atmos_truehd", "Dolby Atmos (TrueHD)"),
        ["dolby_atmos_mat"] = new("atmos", "Dolby Atmos (MAT)"),
        ["dolby_atmos_digital_plus"] = new("atmos", "Dolby Atmos (DD+)"),
        ["dolby_atmos"] = new("atmos", "Dolby Atmos"),
        ["atmos"] = new("atmos", "Dolby Atmos"),

        // Dolby Audio
        ["dolby_digital_truehd"] = new("truehd", "Dolby TrueHD"),
        ["dolby_digital_plus"] = new("ddplus", "Dolby Digital Plus"),
        ["dolby_mat"] = new("ddplus", "Dolby MAT"),
        ["dolby_audio"] = new("ddplus", "Dolby Audio"),
        ["ddplus"] = new("ddplus", "Dolby Digital Plus"),
        ["dd_plus"] = new("ddplus", "Dolby Digital Plus"),
        ["eac3"] = new("ddplus", "Dolby Digital Plus"),
        ["e_ac3"] = new("ddplus", "Dolby Digital Plus"),
        ["e_ac_3"] = new("ddplus", "Dolby Digital Plus"),
        ["dolby_digital"] = new("dd", "Dolby Digital"),
        ["dd"] = new("dd", "Dolby Digital"),
        ["ac3"] = new("dd", "Dolby Digital"),
        ["ac_3"] = new("dd", "Dolby Digital"),

        // DTS and IMAX Enhanced
        ["dts_x"] = new("dtsx", "DTS:X"),
        ["dtsx"] = new("dtsx", "DTS:X"),
        ["dts_x_master_audio"] = new("dtsx", "DTS:X Master Audio"),
        ["dts_x_high_resolution"] = new("dtsx", "DTS:X High Resolution"),
        ["dts_x_imax"] = new("imax", "IMAX Enhanced DTS:X"),
        ["imax_dts_x"] = new("imax", "IMAX Enhanced DTS:X"),
        ["imax_dts"] = new("imax", "IMAX Enhanced DTS"),
        ["dts_hd_master_audio"] = new("dtshd", "DTS-HD MA"),
        ["dts_hd_high_resolution"] = new("dtshd", "DTS-HD HRA"),
        ["dts_hd"] = new("dtshd", "DTS-HD"),
        ["dtshd"] = new("dtshd", "DTS-HD"),
        ["dts_es_6_1_matrix"] = new("dts", "DTS-ES Matrix 6.1"),
        ["dts_es_6_1_discrete"] = new("dts", "DTS-ES Discrete 6.1"),
        ["dts_es_8ch_discrete"] = new("dts", "DTS-ES Discrete 8ch"),
        ["dts_96_24"] = new("dts", "DTS 96/24"),
        ["dts_express"] = new("dts", "DTS Express"),
        ["dts_unknown"] = new("dts", "DTS Digital Surround"),
        ["dts"] = new("dts", "DTS Digital Surround"),

        // Uncompressed, music, and spatial formats
        ["lpcm"] = new("lpcm", "LPCM"),
        ["pcm"] = new("lpcm", "PCM"),
        ["linear_pcm"] = new("lpcm", "Linear PCM"),
        ["multichannel_pcm"] = new("lpcm", "Multichannel PCM"),
        ["mpeg_2_aac"] = new("aac", "MPEG-2 AAC"),
        ["mpeg_4_aac"] = new("aac", "MPEG-4 AAC"),
        ["aac"] = new("aac", "AAC"),
        ["dsd"] = new("dsd", "DSD"),
        ["360_reality_audio"] = new("360ra", "360 Reality Audio"),
        ["360ra"] = new("360ra", "360 Reality Audio"),

        // Non-playing values are normally cleared by BraviaEngine first.
        ["imax_off"] = new("idle", "IMAX Off"),
        ["no_audio"] = new("idle", "No Audio"),
        ["none"] = new("idle", "No Audio"),
        ["unknown"] = new("idle", "Unknown")
    };

    public static string Classify(string? rawCodec)
    {
        var clean = Normalize(rawCodec);
        if (clean.Length == 0)
            return "idle";

        if (CodecMap.TryGetValue(clean, out var info))
            return info.BadgeKind;

        if (clean.Contains("imax") && clean.Contains("dts")) return "imax";
        if (clean.Contains("atmos")) return "atmos";
        if (clean.Contains("truehd")) return "truehd";
        if (clean.Contains("digital_plus") || clean.Contains("ddplus") || clean.Contains("dd_plus") ||
            clean.Contains("eac3") || clean.Contains("e_ac3") || clean.Contains("e_ac_3")) return "ddplus";
        if (clean.Contains("dts_x") || clean.Contains("dtsx")) return "dtsx";
        if (clean.Contains("dts_hd") || clean.Contains("dtshd")) return "dtshd";
        if (clean.Contains("dts")) return "dts";
        if (clean.Contains("dsd")) return "dsd";
        if (clean.Contains("pcm")) return "lpcm";
        if (clean.Contains("dolby") || clean.Contains("ac3") || clean.Contains("ac_3")) return "dd";
        if (clean.Contains("360")) return "360ra";
        if (clean.Contains("aac")) return "aac";

        return "idle";
    }

    public static string FormatHumanReadable(string? rawCodec, string? channel = null)
    {
        if (string.IsNullOrWhiteSpace(rawCodec))
            return "Idle";

        var clean = Normalize(rawCodec);
        var name = CodecMap.TryGetValue(clean, out var info)
            ? info.DisplayName
            : rawCodec.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(channel))
        {
            var ch = channel.Trim();
            if (!ch.EndsWith("ch", StringComparison.OrdinalIgnoreCase))
                ch += " ch";
            return $"{name} ({ch})";
        }

        return name;
    }

    private static string Normalize(string? rawCodec)
    {
        if (string.IsNullOrWhiteSpace(rawCodec)) return string.Empty;

        var source = rawCodec.Trim().Replace("+", " plus ", StringComparison.Ordinal);
        var result = new StringBuilder(source.Length);
        var separatorPending = false;

        foreach (var character in source)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && result.Length > 0)
                    result.Append('_');
                result.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = result.Length > 0;
            }
        }

        return result.ToString();
    }
}
