using Hub.Core.Audio;

namespace Hub.Tests;

public class EqualizerApoImportTests
{
    const string IemTarget = """
        Preamp: -4 dB
        Filter 1: ON LSC Fc 120 Hz Gain -6 dB Q 0.7
        Filter 2: ON PK Fc 250 Hz Gain -2 dB Q 1.0
        Filter 3: ON PK Fc 2000 Hz Gain 3 dB Q 1.2
        Filter 4: ON PK Fc 4000 Hz Gain 2.5 dB Q 1.5
        Filter 5: ON PK Fc 6500 Hz Gain -2 dB Q 3.0
        Filter 6: ON PK Fc 9000 Hz Gain 1.5 dB Q 1.0
        """;

    [Fact]
    public void Reads_the_users_cs2_zero_preset_exactly()
    {
        var r = EqualizerApoImport.Parse(IemTarget);

        Assert.Equal(-4, r.PreampDb);
        Assert.Empty(r.Skipped);
        Assert.Equal(
            [
                new EqFilter(FilterType.LowShelf, 120, -6, 0.7),
                new EqFilter(FilterType.Peaking, 250, -2, 1.0),
                new EqFilter(FilterType.Peaking, 2000, 3, 1.2),
                new EqFilter(FilterType.Peaking, 4000, 2.5, 1.5),
                new EqFilter(FilterType.Peaking, 6500, -2, 3.0),
                new EqFilter(FilterType.Peaking, 9000, 1.5, 1.0),
            ], r.Filters);
    }

    [Fact]
    public void Round_trips_hubs_own_export()
    {
        var preset = new EqPresetData("x", -3.5, [
            new(FilterType.Peaking, 1000, 2, 1.4), new(FilterType.HighShelf, 8000, -3, 0.7071),
            new(FilterType.LowPass, 18000, 0, 0.7071), new(FilterType.HighPass, 20, 0, 0.5), new(FilterType.Notch, 3000, 0, 30)]);
        var r = EqualizerApoImport.Parse(preset.ToEqualizerApo(autoHeadroom: false));

        Assert.Equal(-3.5, r.PreampDb);
        Assert.Empty(r.Skipped);
        Assert.Equal(preset.Filters, r.Filters);
    }

    [Fact]
    public void Understands_equalizer_apo_variations()
    {
        var r = EqualizerApoImport.Parse("""
            # comment line
            Preamp: -2.5 dB
            Preamp: -1 dB
            Filter: OFF PK Fc 1 kHz Gain 4 dB Q 2
            Filter 2: ON PEQ Fc 500 Hz Gain -1 dB Q 0.9
            Filter 3: ON LP Fc 15000 Hz
            Filter 4: ON HP Fc 30 Hz
            Filter 5: ON LS Fc 100 Hz Gain 3 dB
            filter 6: on hsc fc 6000 hz gain 2 db q 0.8

            """);

        Assert.Equal(-3.5, r.PreampDb); // Equalizer APO adds every Preamp line
        Assert.Equal(
            [
                new EqFilter(FilterType.Peaking, 1000, 4, 2, Enabled: false),
                new EqFilter(FilterType.Peaking, 500, -1, 0.9),
                new EqFilter(FilterType.LowPass, 15000, 0, EqualizerApoImport.ButterworthQ),
                new EqFilter(FilterType.HighPass, 30, 0, EqualizerApoImport.ButterworthQ),
                new EqFilter(FilterType.HighShelf, 6000, 2, 0.8),
            ], r.Filters);
        Assert.Equal([8], r.Skipped.Select(s => s.Line)); // LS without Q has a slope Hub can't reproduce exactly
    }

    [Fact]
    public void Lists_lines_hub_cannot_do_instead_of_half_importing_silently()
    {
        var r = EqualizerApoImport.Parse("""
            Copy: L=R
            Channel: all
            Preamp: -6 dB
            Filter 1: ON PK Fc 100 Hz Gain 2 dB BW Oct 1
            Filter 2: ON BP Fc 1000 Hz
            Convolution: ir.wav
            GraphicEQ: 25 0; 40 1
            Filter 3: ON PK Fc 200 Hz Gain 1 dB Q 1
            """);

        Assert.Equal(-6, r.PreampDb);
        Assert.Equal([new EqFilter(FilterType.Peaking, 200, 1, 1)], r.Filters);
        Assert.Equal([1, 4, 5, 6, 7], r.Skipped.Select(s => s.Line));
        Assert.Equal("Copy: L=R", r.Skipped[0].Text);
    }

    [Fact]
    public void Stops_at_lines_that_apply_the_rest_to_part_of_the_audio()
    {
        var r = EqualizerApoImport.Parse("""
            Preamp: -3 dB
            Filter 1: ON PK Fc 100 Hz Gain 2 dB Q 1
            Channel: L
            Preamp: -1 dB
            Filter 2: ON PK Fc 200 Hz Gain 1 dB Q 1
            Channel: R
            Filter 3: ON PK Fc 300 Hz Gain 1 dB Q 1
            """);

        Assert.Equal(-3, r.PreampDb);
        Assert.Equal([new EqFilter(FilterType.Peaking, 100, 2, 1)], r.Filters);
        Assert.Equal([3, 4, 5, 6, 7], r.Skipped.Select(s => s.Line));
    }

    [Fact]
    public void Names_imports_after_the_file()
    {
        Assert.Equal("IEM Target", EqualizerApoImport.NameFromFile(@"C:\Users\x\Downloads\IEM_Target.txt"));
        Assert.Equal("Harman IEM 2019", EqualizerApoImport.NameFromFile("Harman IEM 2019 ParametricEQ.txt"));
        Assert.Equal("Imported", EqualizerApoImport.NameFromFile(".txt"));
    }
}

public class DeviceSetupTests
{
    static readonly string[] Channels = ["Game", "Chat", "Media"];

    static DeviceSetups Setups(out DeviceAudioSetup legacy)
    {
        legacy = new DeviceAudioSetup { Presets = { ["Game"] = "Competitive", ["Media"] = "Flat" }, Tame = { ["Game"] = 4 } };
        return new DeviceSetups(new Dictionary<string, DeviceAudioSetup>(), null, legacy);
    }

    [Fact]
    public void First_device_gets_the_setup_from_before_and_becomes_the_default()
    {
        var s = Setups(out _);
        var headset = s.For("headset", "Speakers (Wireless Headset)");

        Assert.Equal("Competitive", headset.Presets["Game"]);
        Assert.Equal(4, headset.Tame["Game"]);
        Assert.Equal("headset", s.DefaultId);
        Assert.Equal("Speakers (Wireless Headset)", headset.Name);
    }

    [Fact]
    public void A_new_device_copies_the_default_and_then_goes_its_own_way()
    {
        var s = Setups(out _);
        var headset = s.For("headset", "Headset");
        headset.Presets["Media"] = "Music: Rock";

        var iem = s.For("iem", "USB-C adapter");
        Assert.Equal("Music: Rock", iem.Presets["Media"]);

        iem.Presets["Game"] = "IEM Target";
        iem.Tame["Game"] = 0;
        Assert.Equal("Competitive", s.For("headset", "Headset").Presets["Game"]);
        Assert.Equal(4, s.For("headset", "Headset").Tame["Game"]);
        Assert.Equal("headset", s.DefaultId);
    }

    [Fact]
    public void Start_flat_and_make_default()
    {
        var s = Setups(out _);
        s.For("headset", "Headset");
        var iem = s.For("iem", "USB-C adapter");

        s.StartFlat("iem", Channels);
        Assert.All(Channels, c => Assert.Equal("Flat", iem.Presets[c]));
        Assert.Empty(iem.Tame);

        s.MakeDefault("iem");
        Assert.Equal("iem", s.DefaultId);
        Assert.Equal("Flat", s.For("speakers", "Speakers").Presets["Game"]);
        Assert.Throws<ArgumentException>(() => s.MakeDefault("nope"));
    }

    [Fact]
    public void Renaming_or_deleting_a_profile_follows_on_every_device()
    {
        var s = Setups(out var legacy);
        var headset = s.For("headset", "Headset");
        var iem = s.For("iem", "USB-C adapter");
        iem.Presets["Game"] = "Zero";
        headset.Presets["Game"] = "Zero";
        headset.Presets["Chat"] = "Zero"; // another channel's profile with the same name is a different profile

        s.RenameProfile("Game", "Zero", "Zero v2");
        Assert.Equal("Zero v2", iem.Presets["Game"]);
        Assert.Equal("Zero v2", headset.Presets["Game"]);
        Assert.Equal("Zero", headset.Presets["Chat"]);

        s.RemoveProfile("Game", "Zero v2");
        Assert.False(iem.Presets.ContainsKey("Game")); // back to the channel's default preset
        Assert.False(headset.Presets.ContainsKey("Game"));
        Assert.Equal("Zero", headset.Presets["Chat"]);
        Assert.Equal("Competitive", legacy.Presets["Game"]); // the setup from before is left alone
    }

    [Fact]
    public void A_missing_default_falls_back_to_the_setup_from_before()
    {
        var map = new Dictionary<string, DeviceAudioSetup> { ["iem"] = new() { Name = "USB-C adapter" } };
        var legacy = new DeviceAudioSetup { Presets = { ["Game"] = "Competitive" } };
        var s = new DeviceSetups(map, "gone", legacy);

        Assert.Equal("Competitive", s.For("headset", "Headset").Presets["Game"]);
    }
}
