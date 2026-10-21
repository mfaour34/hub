using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hub.App.Native;
using Hub.Core.Audio;
using Hub.Core.Audio.Engine;

namespace Hub.App.Engine;

/// <summary>
/// Hub's own audio engine, with no virtual-device driver: apps play to a silent "sink"
/// device, each app's audio is captured with per-process loopback, mixed into Game/Chat/Media with their EQ and
/// limiter (<see cref="MixGraph"/>) and played to the output device by one real-time thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>Rate apps are captured at and the mix is played at (the headset's own rate, so Windows converts nothing).</summary>
    public const int Rate = 48000;

    /// <summary>Processing rate for the EQ and limiter, 2× <see cref="Rate"/>: filter shapes near 20 kHz stay accurate and the limiter sees inter-sample peaks.</summary>
    public const int DspRate = 2 * Rate;

    const long RenderBufferHns = 200_000; // 20 ms
    const long CaptureBufferHns = 200_000;

    sealed class Capture(int pid, Wasapi.IAudioClient client, Wasapi.IAudioCaptureClient reader, AppStream stream)
    {
        public readonly int Pid = pid;
        public readonly Wasapi.IAudioClient Client = client;
        public readonly Wasapi.IAudioCaptureClient Reader = reader;
        public readonly AppStream Stream = stream;
        public bool Failed;
    }

    readonly string _outputId;
    readonly ConcurrentQueue<Action> _commands = new();
    readonly List<Capture> _captures = [];   // audio thread only
    readonly ConcurrentQueue<Capture> _retired = new();
    readonly Thread _thread;
    volatile bool _stop;
    float[] _scratch = new float[2 * Rate / 5];

    public AudioEngine(string outputEndpointId)
    {
        _outputId = outputEndpointId;
        Graph = new MixGraph(Rate);
        _thread = new Thread(Run) { IsBackground = true, Name = "Hub audio engine", Priority = ThreadPriority.Highest };
    }

    public MixGraph Graph { get; }

    /// <summary>Last error on the output device (null while it plays).</summary>
    public string? OutputError { get; private set; }
    public bool RawOutput { get; private set; }
    public int OutputBufferFrames { get; private set; }
    public int Glitches { get; private set; }

    /// <summary>Apps currently captured.</summary>
    public IReadOnlyCollection<int> Apps => _apps.Keys.ToList();
    readonly ConcurrentDictionary<int, AudioChannel> _apps = new();

    public void Start() => _thread.Start();

    // ---------------- apps (any thread) ----------------

    /// <summary>Starts capturing an app. COM activation runs on a pool (MTA) thread, like the audio thread.</summary>
    public Task AddApp(int pid, AudioChannel channel)
    {
        if (!_apps.TryAdd(pid, channel)) { SetAppChannel(pid, channel); return Task.CompletedTask; }
        return Task.Run(() =>
        {
            try
            {
                var client = Wasapi.ProcessLoopback(pid);
                Wasapi.Initialize(client, Wasapi.StreamLoopback | Wasapi.StreamAutoConvertPcm | Wasapi.StreamSrcDefaultQuality, CaptureBufferHns, Rate);
                var reader = Wasapi.Service<Wasapi.IAudioCaptureClient>(client);
                var capture = new Capture(pid, client, reader, new AppStream(Rate));
                Marshal.ThrowExceptionForHR(client.Start());
                _commands.Enqueue(() =>
                {
                    _captures.Add(capture);
                    Graph.AddSource(pid, capture.Stream, _apps.GetValueOrDefault(pid, channel));
                });
            }
            catch (Exception)
            {
                _apps.TryRemove(pid, out _);
                throw;
            }
        });
    }

    public void SetAppChannel(int pid, AudioChannel channel)
    {
        if (_apps.ContainsKey(pid)) _apps[pid] = channel;
        Graph.SetSourceChannel(pid, channel);
    }

    public void RemoveApp(int pid)
    {
        if (!_apps.TryRemove(pid, out _)) return;
        _commands.Enqueue(() =>
        {
            Graph.RemoveSource(pid);
            int i = _captures.FindIndex(c => c.Pid == pid);
            if (i >= 0) { _retired.Enqueue(_captures[i]); _captures.RemoveAt(i); }
        });
    }

    /// <summary>Releases captures and limiters the audio thread dropped. Call from a non-real-time thread now and then.</summary>
    public void CollectGarbage()
    {
        while (_retired.TryDequeue(out var c))
        {
            try { c.Client.Stop(); } catch (Exception) { }
            Marshal.ReleaseComObject(c.Reader);
            Marshal.ReleaseComObject(c.Client);
        }
        Graph.CollectGarbage();
    }

    // ---------------- audio thread ----------------

    void Run()
    {
        uint task = 0;
        var mmcss = Wasapi.AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
        try
        {
            while (!_stop)
            {
                try { RunOutput(); OutputError = null; }
                catch (Exception ex)
                {
                    // the output device went away or changed format: drain the apps meanwhile and retry
                    OutputError = ex.Message;
                    for (int i = 0; i < 50 && !_stop; i++) { Drain(); Thread.Sleep(20); }
                }
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) Wasapi.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    void RunOutput()
    {
        var device = Wasapi.Device(_outputId);
        var client = Wasapi.Activate<Wasapi.IAudioClient>(device);
        using var ready = new AutoResetEvent(false);
        try
        {
            RawOutput = Wasapi.TryRaw(client);
            Wasapi.Initialize(client, Wasapi.StreamEventCallback | Wasapi.StreamAutoConvertPcm | Wasapi.StreamSrcDefaultQuality, RenderBufferHns, Rate);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint bufferFrames));
            OutputBufferFrames = (int)bufferFrames;
            var writer = Wasapi.Service<Wasapi.IAudioRenderClient>(client);
            if (_scratch.Length < bufferFrames * 2) _scratch = new float[bufferFrames * 2];
            Marshal.ThrowExceptionForHR(client.Start());
            try
            {
                while (!_stop)
                {
                    if (!ready.WaitOne(200)) Glitches++; // the device stopped asking for audio
                    Drain();
                    Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out uint padding));
                    int frames = (int)(bufferFrames - padding);
                    if (frames <= 0) continue;
                    if (padding == 0) Glitches++; // the buffer had run empty: an audible gap
                    var span = _scratch.AsSpan(0, frames * 2);
                    Graph.Render(span, frames);
                    Marshal.ThrowExceptionForHR(writer.GetBuffer((uint)frames, out var data));
                    Marshal.Copy(_scratch, 0, data, frames * 2);
                    Marshal.ThrowExceptionForHR(writer.ReleaseBuffer((uint)frames, 0));
                }
            }
            finally
            {
                client.Stop();
                Marshal.ReleaseComObject(writer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Moves every captured packet into its app stream (audio thread).</summary>
    void Drain()
    {
        while (_commands.TryDequeue(out var c)) c();
        foreach (var cap in _captures)
        {
            if (cap.Failed) continue;
            try
            {
                while (cap.Reader.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    Marshal.ThrowExceptionForHR(cap.Reader.GetBuffer(out var data, out uint frames, out uint flags, out _, out _));
                    var span = _scratch.AsSpan(0, (int)Math.Min(frames * 2, (uint)_scratch.Length));
                    if ((flags & Wasapi.BufferSilent) != 0) span.Clear();
                    else Marshal.Copy(data, _scratch, 0, span.Length);
                    cap.Reader.ReleaseBuffer(frames);
                    cap.Stream.Write(span);
                }
            }
            catch (Exception) { cap.Failed = true; } // the app exited; the session watcher removes it
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(2000);
        foreach (var c in _captures) _retired.Enqueue(c);
        _captures.Clear();
        CollectGarbage();
        Graph.Dispose();
    }
}
