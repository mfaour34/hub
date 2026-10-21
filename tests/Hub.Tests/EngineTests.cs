using Hub.Core.Audio;
using Hub.Core.Audio.Engine;

namespace Hub.Tests;

public class EngineTests
{
    const int Rate = 96000;   // channel DSP rate
    const int Io = 48000;     // app streams and mix rate

    static readonly EqPresetData Cs2 = new("CS2 Pro Preset", 0,
    [
        new(FilterType.HighPass, 100, 0, 0.707), new(FilterType.Peaking, 200, 5, 1.5), new(FilterType.Peaking, 350, 5, 2),
        new(FilterType.Peaking, 5123, 4, 10), new(FilterType.Peaking, 6466, 4, 10), new(FilterType.Peaking, 7700, 4, 10),
        new(FilterType.HighShelf, 4000, 5, 0.707), new(FilterType.Peaking, 2994, -6, 10), new(FilterType.Peaking, 2000, 5, 2),
    ]);

    static double RmsDb(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (var v in x) s += v * (double)v;
        return 10 * Math.Log10(s / x.Length + 1e-30);
    }

    static float[] Sine(double f, int n, double amp = 0.1, int offset = 0) =>
        Enumerable.Range(offset, n).Select(i => (float)(amp * Math.Sin(2 * Math.PI * f * i / Rate))).ToArray();

    [Theory]
    [InlineData(50)] [InlineData(200)] [InlineData(1000)] [InlineData(2994)] [InlineData(5123)] [InlineData(7700)] [InlineData(15000)]
    public void Eq_chain_matches_the_biquad_response_at_96k(double f)
    {
        var eq = new EqChain(Cs2, Rate, 0);
        var l = Sine(f, Rate); var r = Sine(f, Rate);
        eq.Process(l, r);
        double measured = RmsDb(l.AsSpan(Rate / 2)) - RmsDb(Sine(f, Rate / 2, offset: Rate / 2));
        double theory = Cs2.Filters.Sum(x => Biquad.For(x, Rate).MagnitudeDb(f, Rate));
        Assert.InRange(measured - theory, -0.05, 0.05);
        Assert.Equal(l, r);
    }

    [Fact]
    public void Preamp_follows_the_equalizer_apo_rules()
    {
        Assert.Equal(0, EqChain.Preamp(Cs2, limiter: true, autoHeadroom: true, Rate));
        Assert.Equal(-Cs2.PeakBoostDb(Rate), EqChain.Preamp(Cs2, limiter: false, autoHeadroom: true, Rate), 6);
        Assert.Equal(0, EqChain.Preamp(Cs2, limiter: false, autoHeadroom: false, Rate));
    }

    /// <summary>Feeds an app stream in capture-sized packets and reads it in output periods, with the capture clock off by <paramref name="ppm"/>.</summary>
    static (AppStream Stream, List<float> Out) Run(Func<int, float> signal, double seconds, double ppm, int seed = 1)
    {
        var s = new AppStream(Io);
        var rnd = new Random(seed);
        var output = new List<float>();
        int period = Io / 100; // 10 ms output periods
        double produced = 0; int written = 0;
        var l = new float[period]; var r = new float[period];
        for (int tick = 0; tick < seconds * 100; tick++)
        {
            // capture delivers its frames in jittery packets (5–15 ms worth), at its own clock
            produced += period * (1 + ppm * 1e-6);
            while (written + 240 <= produced)
            {
                int n = Math.Min((int)produced - written, rnd.Next(240, 720));
                var packet = new float[n * 2];
                for (int i = 0; i < n; i++) packet[2 * i] = packet[2 * i + 1] = signal(written + i);
                s.Write(packet);
                written += n;
            }
            s.Read(l, r);
            output.AddRange(l);
        }
        return (s, output);
    }

    [Theory]
    [InlineData(0)] [InlineData(250)] [InlineData(-250)] [InlineData(1000)]
    public void App_stream_tracks_clock_drift_without_dropouts(double ppm)
    {
        var (s, _) = Run(i => 0.1f, 180, ppm);

        Assert.Equal(0, s.Overruns);
        Assert.InRange(s.Underruns, 0, 1); // at most the start-up one
        Assert.InRange(s.Backlog, s.TargetFrames / 3, s.TargetFrames * 3);
        Assert.InRange(s.Ratio, 1 + (ppm - 120) * 1e-6, 1 + (ppm + 120) * 1e-6);
    }

    [Fact]
    public void App_stream_starts_on_target_after_a_large_first_packet()
    {
        var s = new AppStream(Io);
        s.Write(new float[2 * Io / 10]); // 100 ms arrives at once
        var l = new float[480]; var r = new float[480];
        s.Read(l, r);
        Assert.Equal(s.TargetFrames, s.Backlog);
        Assert.Equal(1.0, s.Ratio, 4);
    }

    [Theory]
    [InlineData(1000)] [InlineData(10000)] [InlineData(20000)]
    public void App_stream_resampling_is_flat_in_the_audio_band(double f)
    {
        var (_, output) = Run(i => (float)(0.1 * Math.Sin(2 * Math.PI * f * i / Io)), 60, 300);
        var tail = output.Skip(output.Count - Io * 5).ToArray();
        double db = RmsDb(tail) - 20 * Math.Log10(0.1 / Math.Sqrt(2));
        Assert.InRange(db, -0.05, 0.05);
    }

    sealed class Gain(float g) : IBlockLimiter
    {
        public bool Disposed;
        public void Process(Span<float> l, Span<float> r) { for (int i = 0; i < l.Length; i++) { l[i] *= g; r[i] *= g; } }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Mix_graph_sums_apps_per_channel_and_applies_each_channels_chain()
    {
        var g = new MixGraph(Io);
        var flat = new EqChain(EqPresetData.Flat(), Rate, 0);
        g.SetBus(AudioChannel.Game, new BusSettings(flat, new Gain(0.5f)));
        g.SetBus(AudioChannel.Chat, new BusSettings(new EqChain(EqPresetData.Flat(), Rate, 0), null));
        var game = new AppStream(Io); var chat = new AppStream(Io);
        g.AddSource(1, game, AudioChannel.Game);
        g.AddSource(2, chat, AudioChannel.Chat);
        var dc = Enumerable.Repeat(0.2f, 2 * 480).ToArray(); // one period of DC per app per render
        var out1 = new float[2 * 480];
        void Render() { game.Write(dc); chat.Write(dc); g.Render(out1, 480); }
        for (int i = 0; i < 4; i++) Render(); // past the oversampler's settling
        // game 0.2 × limiter stand-in 0.5 + chat 0.2
        Assert.Equal(0.3f, out1[^2], 3);

        g.SetBusGain(AudioChannel.Chat, 0);
        Render(); Render();
        Assert.Equal(0.1f, out1[^1], 3);

        g.SetSourceChannel(2, AudioChannel.Game);
        Render(); Render();
        // both apps now on Game: (0.2 + 0.2) × 0.5
        Assert.Equal(0.2f, out1[^1], 3);
    }

    [Fact]
    public void Mix_graph_retires_replaced_limiters_off_the_audio_thread()
    {
        var g = new MixGraph(Io);
        var first = new Gain(1);
        g.SetBus(AudioChannel.Game, new BusSettings(new EqChain(EqPresetData.Flat(), Rate, 0), first));
        g.SetBus(AudioChannel.Game, new BusSettings(new EqChain(EqPresetData.Flat(), Rate, 0), new Gain(1)));
        g.Render(new float[2], 1);
        Assert.False(first.Disposed);
        g.CollectGarbage();
        Assert.True(first.Disposed);
    }

    [Fact]
    public void Output_never_exceeds_full_scale()
    {
        var g = new MixGraph(Io);
        var s = new AppStream(Io);
        g.AddSource(1, s, AudioChannel.Game);
        s.Write(Enumerable.Repeat(3f, 4000).ToArray());
        var o = new float[2 * 1000];
        g.Render(o, 1000);
        Assert.All(o, v => Assert.InRange(v, -1f, 1f));
    }

    [Theory]
    [InlineData(100)] [InlineData(1000)] [InlineData(10000)] [InlineData(18000)] [InlineData(20000)]
    public void Oversampling_round_trip_is_flat_to_20k(double f)
    {
        var os = new Oversampler();
        int n = Io;
        var l = Enumerable.Range(0, n).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * f * i / Io))).ToArray();
        var r = (float[])l.Clone();
        var l2 = new float[2 * n]; var r2 = new float[2 * n];
        var ol = new float[n]; var or = new float[n];
        os.Up(l, r, l2, r2);
        os.Down(l2, r2, ol, or);
        double db = RmsDb(ol.AsSpan(n / 2)) - RmsDb(l.AsSpan(n / 2));
        Assert.InRange(db, -0.02, 0.02);
    }

    [Fact]
    public void Oversampled_signal_has_no_image_above_24k()
    {
        var os = new Oversampler();
        int n = Io;
        var l = Enumerable.Range(0, n).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 15000 * i / Io))).ToArray();
        var l2 = new float[2 * n]; var r2 = new float[2 * n];
        os.Up(l, l, l2, r2);
        // the zero-stuffing image of 15 kHz sits at 33 kHz; measure it with a single-bin DFT at 96 kHz
        double re = 0, im = 0;
        for (int i = n; i < 2 * n; i++) { re += l2[i] * Math.Cos(2 * Math.PI * 33000 * i / 96000.0); im += l2[i] * Math.Sin(2 * Math.PI * 33000 * i / 96000.0); }
        double image = 20 * Math.Log10(2 * Math.Sqrt(re * re + im * im) / n / 0.5);
        Assert.True(image < -75, $"image at {image:0.0} dB");
    }

    [Theory]
    [InlineData("Discord", AudioChannel.Chat)]
    [InlineData("Spotify", AudioChannel.Media)]
    [InlineData("cs2", AudioChannel.Game)]
    [InlineData("brave", AudioChannel.Game)]
    public void Apps_default_to_their_usual_channel(string app, AudioChannel expected) =>
        Assert.Equal(expected, AppChannels.For(app, new Dictionary<string, string>()));

    [Fact]
    public void User_choice_overrides_the_default_channel() =>
        Assert.Equal(AudioChannel.Media, AppChannels.For("Brave", new Dictionary<string, string> { ["brave"] = "media" }));
}
