namespace Hub.Core.Audio;

public enum AudioChannel { Game, Chat, Media }

/// <summary>The read-only presets of one channel and the one selected until the user picks another.</summary>
public sealed record ChannelPresets(AudioChannel Channel, IReadOnlyList<EqPresetData> Presets, string DefaultName);

/// <summary>
/// Hub's own starting points for each channel. They are deliberately gentle (a few dB, broad Q) so they suit most
/// headphones; anything more specific belongs in a custom profile made from one of these.
/// </summary>
public static class BuiltInPresets
{
    static EqFilter Pk(double f, double g, double q) => new(FilterType.Peaking, f, g, q);
    static EqFilter Ls(double f, double g, double q = 0.707) => new(FilterType.LowShelf, f, g, q);
    static EqFilter Hs(double f, double g, double q = 0.707) => new(FilterType.HighShelf, f, g, q);
    static EqFilter Hp(double f, double q = 0.707) => new(FilterType.HighPass, f, 0, q);

    public static IReadOnlyList<ChannelPresets> All { get; } =
    [
        new(AudioChannel.Game,
        [
            EqPresetData.Flat(),
            // Lifts the 2–5 kHz band where footsteps and reloads sit; trims rumble that masks them.
            new("Competitive", 0, [Hp(30), Ls(120, -3), Pk(2500, 3, 1.2), Pk(4500, 3.5, 1.5), Hs(9000, 1.5)]),
            new("Immersive", 0, [Ls(80, 4), Pk(400, -1, 1), Pk(3000, 1.5, 1), Hs(10000, 2)]),
            // For late nights at low volume: less boom, clearer detail.
            new("Night", 0, [Ls(150, -4), Pk(2500, 2, 1), Hs(8000, 1)]),
        ], "Competitive"),
        new(AudioChannel.Chat,
        [
            EqPresetData.Flat(),
            new("Voice clarity", 0, [Hp(100), Pk(300, -2, 1), Pk(2500, 3, 1), Pk(5000, 2, 1.5)]),
            new("Warm voice", 0, [Hp(80), Pk(200, 2, 1), Hs(6000, -2)]),
        ], "Flat"),
        new(AudioChannel.Media,
        [
            EqPresetData.Flat(),
            new("Music", 0, [Ls(100, 2.5), Pk(1000, -0.5, 0.7), Hs(9000, 2)]),
            new("Bass boost", 0, [Ls(90, 6), Pk(250, -1.5, 1)]),
            new("Movies", 0, [Ls(70, 4), Pk(2500, 2, 1.2), Hs(10000, 1.5)]),
            new("Podcasts", 0, [Hp(80), Pk(250, -1.5, 1), Pk(3000, 3, 1)]),
        ], "Music"),
    ];
}

/// <summary>ChatMix: balance -1 (game only) .. 0 (both full) .. +1 (chat only).</summary>
public static class ChatMix
{
    public static (double Game, double Chat) Gains(double balance)
    {
        double b = Math.Clamp(balance, -1, 1);
        double s = Math.Sign(b) * 2 * Math.Abs(b) / (Math.Abs(b) + 1);
        return (Math.Clamp(1 - s, 0, 1), Math.Clamp(1 + s, 0, 1));
    }

    /// <summary>Linear gain to a VoiceMeeter fader value in dB (-60 = silence).</summary>
    public static double ToFaderDb(double linear) => linear <= 0.001 ? -60 : Math.Max(-60, 20 * Math.Log10(linear));
}
