using Hub.App.Native;
using Hub.Core.Audio;
using Hub.Core.Audio.Engine;

namespace Hub.App.Engine;

/// <summary>
/// Runs the real engine between two devices nobody listens to (e.g. Steam Streaming Speakers → Steam Streaming
/// Microphone's playback side) without touching Windows defaults, so it can be measured with loopback captures.
/// Refuses headphones and speakers by name.
/// </summary>
static class EngineTest
{
    public static int Run(string outputName, string sinkName, double seconds, string log, string? onlyApp = null)
    {
        using var w = new StreamWriter(log) { AutoFlush = true };
        void Line(string s) { Console.WriteLine(s); w.WriteLine(s); }
        try
        {
            var all = AudioEndpoints.List().Where(e => e.IsRender && e.Active).ToList();
            var output = all.First(e => e.Name.StartsWith(outputName, StringComparison.OrdinalIgnoreCase));
            var sink = all.First(e => e.Name.StartsWith(sinkName, StringComparison.OrdinalIgnoreCase));
            if (!output.Name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"refusing to test on an audible device: {output.Name}");
            Line($"output {output.Name}  sink {sink.Name}");
            // the engine plays at 48 kHz like the headset; the test device must too, so Windows converts nothing
            AudioEndpoints.SetSampleRate(output.Id, AudioEngine.Rate);

            var settings = AppSettings.Load();
            var presets = new AudioService(settings);
            using var engine = new AudioEngine(output.Id);
            foreach (var ch in Enum.GetValues<AudioChannel>())
            {
                var preset = presets.ActivePreset(ch);
                var chain = presets.ActiveChain(ch);
                var eq = new EqChain(preset, AudioEngine.DspRate, EqChain.Preamp(preset, chain.Limiter, chain.AutoHeadroom, AudioEngine.DspRate));
                engine.Graph.SetBus(ch, new BusSettings(eq, chain.Limiter ? new NativeLimiter(AudioEngine.DspRate) : null));
                Line($"{ch}: {preset.Name}, preamp {eq.PreampDb:0.0} dB, limiter {chain.Limiter}");
            }
            engine.Start();
            var watcher = new AppWatcher(engine, sink.Guid, name => AppChannels.For(name, settings.AppChannels),
                name => onlyApp is null || name.Equals(onlyApp, StringComparison.OrdinalIgnoreCase));
            var start = DateTime.UtcNow;
            var nextLog = 1.0;
            while ((DateTime.UtcNow - start).TotalSeconds < seconds)
            {
                watcher.Poll();
                Thread.Sleep(1000);
                double t = (DateTime.UtcNow - start).TotalSeconds;
                if (t < nextLog) continue;
                nextLog += 1;
                var stats = string.Join("  ", engine.Graph.Stats.Select(s => $"[{s.Id} {s.Channel} backlog {s.Backlog} ratio {(s.Ratio - 1) * 1e6:+0;-0} ppm u{s.Underruns} o{s.Overruns}]"));
                Line($"{t,5:0}s raw={engine.RawOutput} buf={engine.OutputBufferFrames} glitches={engine.Glitches} err={engine.OutputError ?? "-"} {watcher.LastError ?? ""} {stats}");
            }
            return 0;
        }
        catch (Exception ex) { Line("FAIL " + ex); return 1; }
    }
}
