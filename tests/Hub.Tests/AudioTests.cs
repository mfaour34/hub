using Hub.Core.Audio;

namespace Hub.Tests;

public class AudioTests
{
    static EqPresetData Preset(AudioChannel ch, string name) =>
        BuiltInPresets.All.Single(c => c.Channel == ch).Presets.Single(p => p.Name == name);

    [Fact]
    public void Every_channel_has_flat_and_a_valid_default()
    {
        Assert.Equal([AudioChannel.Game, AudioChannel.Chat, AudioChannel.Media], BuiltInPresets.All.Select(c => c.Channel));
        foreach (var c in BuiltInPresets.All)
        {
            Assert.Contains(c.Presets, p => p.Name == "Flat" && p.Filters.Count == 0);
            Assert.Contains(c.Presets, p => p.Name == c.DefaultName);
            Assert.Equal(c.Presets.Count, c.Presets.Select(p => p.Name).Distinct().Count());
        }
    }

    [Fact]
    public void Built_in_presets_stay_gentle()
    {
        // Starting points must suit most headphones: no extreme boosts, cuts or narrow filters.
        foreach (var p in BuiltInPresets.All.SelectMany(c => c.Presets))
        {
            Assert.InRange(p.PeakBoostDb(), 0, 6.5);
            Assert.All(p.Filters, f => { Assert.InRange(f.GainDb, -6, 6); Assert.InRange(f.Q, 0.5, 2); Assert.InRange(f.Frequency, 20, 20000); });
        }
    }

    [Fact]
    public void Equalizer_apo_export_adds_headroom_for_the_peak_boost()
    {
        var music = Preset(AudioChannel.Media, "Music");

        var text = music.ToEqualizerApo();

        Assert.Contains("Filter: ON LSC Fc 100 Hz Gain 2.5 dB Q 0.707", text);
        var preamp = double.Parse(text.Split('\n')[1].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(-music.PeakBoostDb(), preamp, 1);
        Assert.True(preamp < -1.5);
    }

    [Fact]
    public void With_the_limiter_the_preset_runs_at_its_own_gain()
    {
        var competitive = Preset(AudioChannel.Game, "Competitive");

        var text = competitive.ToEqualizerApo(@"C:\eapo\hub\hub-limiter.dll");

        Assert.Contains("Preamp: 0.0 dB", text);
        Assert.EndsWith("VSTPlugin: Library \"C:\\eapo\\hub\\hub-limiter.dll\"" + Environment.NewLine, text);
    }

    [Fact]
    public void Without_auto_headroom_the_preset_keeps_its_own_gain()
    {
        var music = Preset(AudioChannel.Media, "Music");

        var text = (music with { GainDb = 1.5 }).ToEqualizerApo(autoHeadroom: false);

        Assert.Contains("Preamp: 1.5 dB", text);
        Assert.DoesNotContain("VSTPlugin", text);
    }

    [Fact]
    public void Disabled_filters_are_left_out_of_the_export()
    {
        var p = new EqPresetData("x", 0, [new EqFilter(FilterType.Peaking, 1000, 3, 1), new EqFilter(FilterType.Peaking, 2000, 6, 1, Enabled: false)]);

        var text = p.ToEqualizerApo("lim.dll");

        Assert.Contains("Fc 1000 Hz", text);
        Assert.DoesNotContain("Fc 2000 Hz", text);
    }

    [Fact]
    public void Filters_round_trip_through_json_for_custom_profiles()
    {
        List<EqFilter> filters = [new(FilterType.HighShelf, 4000, 5, 0.707), new(FilterType.Notch, 50, 0, 10, false)];

        var back = System.Text.Json.JsonSerializer.Deserialize<List<EqFilter>>(System.Text.Json.JsonSerializer.Serialize(filters));

        Assert.Equal(filters, back);
    }

    [Fact]
    public void Flat_preset_has_no_preamp()
    {
        Assert.Contains("Preamp: 0.0 dB", EqPresetData.Flat().ToEqualizerApo());
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(-1, 1, 0)]
    [InlineData(0.5, 1 - 2.0 / 3, 1)]
    public void Chatmix_keeps_both_full_at_centre_and_fades_one_side(double balance, double game, double chat)
    {
        var (g, c) = ChatMix.Gains(balance);
        Assert.Equal(game, g, 6);
        Assert.Equal(chat, c, 6);
    }
}
