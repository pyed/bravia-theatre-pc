namespace BraviaTheatre.Core.Models;

public sealed record SoundbarState
{
    public bool Connected { get; init; }
    public bool Power { get; init; }
    public int Volume { get; init; }
    public bool Mute { get; init; }
    public bool NightMode { get; init; }
    public bool SoundField { get; init; }
    public bool VoiceMode { get; init; }
    public string Bass { get; init; } = "mid";
    public int RearLevel { get; init; } = 0;
    public string Function { get; init; } = "hdmi";
    public string? Codec { get; init; }
    public string? Channel { get; init; }
    public string? Input { get; init; }
    public bool AuthRequired { get; init; }
    public string? DeviceName { get; init; }

    public string CodecBadgeKind => !Connected ? "idle" : Power ? CodecTaxonomy.Classify(Codec) : "standby";
    public string HumanCodec => !Connected ? "Offline" : Power ? CodecTaxonomy.FormatHumanReadable(Codec, Channel) : "Standby";

    public static SoundbarState Disconnected => new()
    {
        Connected = false,
        Power = false,
        Volume = 0,
        Mute = false,
        NightMode = false,
        SoundField = false,
        VoiceMode = false,
        Bass = "mid",
        RearLevel = 0,
        Function = "hdmi",
        Codec = null,
        Channel = null,
        Input = null,
        DeviceName = "BRAVIA Theatre",
        AuthRequired = false
    };
}
