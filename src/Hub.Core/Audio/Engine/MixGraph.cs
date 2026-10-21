using System.Collections.Concurrent;

namespace Hub.Core.Audio.Engine;

/// <summary>A stereo look-ahead limiter working in place on a block (the native limiter in Hub.App).</summary>
public interface IBlockLimiter : IDisposable
{
    void Process(Span<float> left, Span<float> right);
}

/// <summary>How one channel is processed at the DSP rate (2× the mix rate): EQ with its preamp, then an optional limiter.</summary>
public sealed record BusSettings(EqChain Eq, IBlockLimiter? Limiter);

/// <summary>
/// Hub's mixer: app streams are summed into Game/Chat/Media,
/// each channel goes through its EQ and limiter and its volume × ChatMix gain, and the channels are summed to the
/// output. Everything here runs on the real-time audio thread; other threads change it only by posting commands,
/// so the audio path never takes a lock or waits.
/// </summary>
public sealed class MixGraph : IDisposable
{
    public const int MaxFrames = 48000 / 10; // largest block rendered at once (100 ms at 48 kHz)

    sealed class Bus
    {
        public BusSettings? Settings;
        public float Gain = 1, TargetGain = 1;
        public readonly float[] L = new float[MaxFrames], R = new float[MaxFrames];
        public readonly float[] L2 = new float[2 * MaxFrames], R2 = new float[2 * MaxFrames];
        public readonly Oversampler Oversampler = new();
        public int Sources;
    }

    sealed class Source(AppStream stream, AudioChannel channel)
    {
        public readonly AppStream Stream = stream;
        public AudioChannel Channel = channel;
    }

    readonly ConcurrentQueue<Action> _commands = new();
    readonly ConcurrentQueue<IDisposable> _retired = new();
    readonly Dictionary<int, Source> _sources = [];
    readonly Bus[] _buses = [new(), new(), new()];
    readonly float[] _tmpL = new float[MaxFrames], _tmpR = new float[MaxFrames];
    float _master = 1, _targetMaster = 1;

    /// <param name="sampleRate">Input/output rate (48 kHz); channel DSP runs at twice this.</param>
    public MixGraph(int sampleRate) => SampleRate = sampleRate;

    public int SampleRate { get; }
    public int DspRate => 2 * SampleRate;

    // ---------------- control side (any thread) ----------------

    public void AddSource(int id, AppStream stream, AudioChannel channel) => _commands.Enqueue(() => _sources[id] = new Source(stream, channel));

    public void RemoveSource(int id) => _commands.Enqueue(() => _sources.Remove(id));

    public void SetSourceChannel(int id, AudioChannel channel) =>
        _commands.Enqueue(() => { if (_sources.TryGetValue(id, out var s)) s.Channel = channel; });

    /// <summary>Replaces a channel's EQ/limiter. The old limiter is disposed off the audio thread (see <see cref="CollectGarbage"/>).</summary>
    public void SetBus(AudioChannel channel, BusSettings settings) => _commands.Enqueue(() =>
    {
        var bus = _buses[(int)channel];
        if (bus.Settings?.Limiter is { } old && !ReferenceEquals(old, settings.Limiter)) _retired.Enqueue(old);
        bus.Settings = settings;
    });

    public void SetBusGain(AudioChannel channel, double gain) => _commands.Enqueue(() => _buses[(int)channel].TargetGain = (float)gain);

    public void SetMasterGain(double gain) => _commands.Enqueue(() => _targetMaster = (float)gain);

    /// <summary>Disposes limiters the audio thread has let go of. Call from a non-real-time thread.</summary>
    public void CollectGarbage()
    {
        while (_retired.TryDequeue(out var d)) d.Dispose();
    }

    // ---------------- audio thread ----------------

    /// <summary>Renders <paramref name="frames"/> interleaved stereo frames into <paramref name="output"/>.</summary>
    public void Render(Span<float> output, int frames)
    {
        while (_commands.TryDequeue(out var c)) c();

        for (int done = 0; done < frames; done += MaxFrames)
        {
            int n = Math.Min(MaxFrames, frames - done);
            RenderBlock(output.Slice(done * 2, n * 2), n);
        }
        UpdateStats(frames);
    }

    void RenderBlock(Span<float> output, int n)
    {
        foreach (var bus in _buses) { bus.L.AsSpan(0, n).Clear(); bus.R.AsSpan(0, n).Clear(); bus.Sources = 0; }
        var tl = _tmpL.AsSpan(0, n);
        var tr = _tmpR.AsSpan(0, n);
        foreach (var s in _sources.Values)
        {
            s.Stream.Read(tl, tr);
            var bus = _buses[(int)s.Channel];
            bus.Sources++;
            for (int i = 0; i < n; i++) { bus.L[i] += tl[i]; bus.R[i] += tr[i]; }
        }

        output.Clear();
        float master0 = _master, master1 = _targetMaster;
        foreach (var bus in _buses)
        {
            var l = bus.L.AsSpan(0, n);
            var r = bus.R.AsSpan(0, n);
            // a channel no app plays on costs nothing (its filters keep their state for when one does)
            if (bus.Sources == 0) { bus.Gain = bus.TargetGain; continue; }
            if (bus.Settings is { } st)
            {
                var l2 = bus.L2.AsSpan(0, 2 * n);
                var r2 = bus.R2.AsSpan(0, 2 * n);
                bus.Oversampler.Up(l, r, l2, r2);
                st.Eq.Process(l2, r2);
                st.Limiter?.Process(l2, r2);
                bus.Oversampler.Down(l2, r2, l, r);
            }
            // linear ramps over the block: fader and ChatMix moves don't zipper
            float g0 = bus.Gain, g1 = bus.TargetGain;
            for (int i = 0; i < n; i++)
            {
                float t = (i + 1) / (float)n;
                float g = g0 + (g1 - g0) * t;
                output[2 * i] += l[i] * g;
                output[2 * i + 1] += r[i] * g;
            }
            bus.Gain = g1;
        }
        for (int i = 0; i < n; i++)
        {
            float t = (i + 1) / (float)n;
            float g = master0 + (master1 - master0) * t;
            output[2 * i] = Math.Clamp(output[2 * i] * g, -1f, 1f);
            output[2 * i + 1] = Math.Clamp(output[2 * i + 1] * g, -1f, 1f);
        }
        _master = master1;
    }

    public sealed record StreamStats(int Id, AudioChannel Channel, int Backlog, double Ratio, int Underruns, int Overruns);

    volatile IReadOnlyList<StreamStats> _stats = [];
    int _sinceStats;

    /// <summary>Per-stream health for diagnostics, refreshed by the audio thread about twice a second.</summary>
    public IReadOnlyList<StreamStats> Stats => _stats;

    void UpdateStats(int frames)
    {
        if ((_sinceStats += frames) < SampleRate / 2) return;
        _sinceStats = 0;
        _stats = _sources.Select(kv => new StreamStats(kv.Key, kv.Value.Channel, kv.Value.Stream.Backlog, kv.Value.Stream.Ratio,
            kv.Value.Stream.Underruns, kv.Value.Stream.Overruns)).ToList();
    }

    public void Dispose()
    {
        foreach (var bus in _buses) bus.Settings?.Limiter?.Dispose();
        CollectGarbage();
    }
}
