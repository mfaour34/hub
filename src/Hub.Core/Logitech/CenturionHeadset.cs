using Hub.Core.Hid;

namespace Hub.Core.Logitech;

public static class CenturionFeature
{
    public const ushort Root = 0x0000, FeatureSet = 0x0001, Bridge = 0x0003, DeviceInfo = 0x0100, DeviceName = 0x0101,
        BatterySoc = 0x0104, AutoSleep = 0x0108, MicSnr = 0x0602, Sidetone = 0x0604, OnboardEq = 0x0636;
}

public sealed class CenturionException(string message) : Exception(message);

public sealed record HeadsetBattery(int Percent, bool Charging);

/// <summary>Feature groups a Centurion headset exposes. <paramref name="Eq"/> also requires the verified 5-band layout.</summary>
public sealed record HeadsetCapabilities(bool Battery, bool Sleep, bool Sidetone, bool NoiseReduction, bool Eq);

/// <summary>
/// Logitech headsets on the Centurion protocol (verified on the PRO X 2 LIGHTSPEED; see docs/protocols.md).
/// Features are looked up by id, so other Centurion headsets work for whatever features they expose.
/// All requests go one at a time; unsolicited events (software id 0) are raised as .NET events.
/// </summary>
public sealed class CenturionHeadset : IDisposable
{
    public const ushort VendorId = 0x046D;
    public const ushort ProductId = 0x0AF7;
    public const ushort ControlUsagePage = 0xFFA0;

    /// <summary>Our software id (any non-zero value distinct from other apps; 0 marks unsolicited events).</summary>
    const byte SoftwareId = 0x7;
    static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1500);

    readonly IReportChannel _channel;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<ushort, byte> _subFeatures = [];
    readonly object _pendingLock = new();
    Pending? _pending;
    byte _bridgeIndex;

    public event Action<HeadsetBattery>? BatteryChanged;
    public event Action<int>? SidetoneChanged;
    public event Action<bool>? NoiseReductionChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action? Disconnected;

    public bool HeadsetConnected { get; private set; }

    public CenturionHeadset(IReportChannel channel)
    {
        _channel = channel;
        _channel.FrameReceived += OnFrame;
        _channel.Closed += () => Disconnected?.Invoke();
    }

    /// <summary>Finds the bridge and reads whether the headset is on.</summary>
    public async Task InitializeAsync()
    {
        var bridge = await DirectAsync(0x00, 0, [CenturionFeature.Bridge >> 8, CenturionFeature.Bridge & 0xFF]);
        if (bridge.Length == 0 || bridge[0] == 0) throw new CenturionException("Dongle has no bridge feature");
        _bridgeIndex = bridge[0];
        var info = await DirectAsync(_bridgeIndex, 0, []);
        HeadsetConnected = info.Length >= 4 && (info[2] != 0 || info[3] != 0);
    }

    // ---- features ----

    /// <summary>Whether the headset exposes a feature (the root lookup is read-only and cached).</summary>
    public async Task<bool> HasFeatureAsync(ushort featureId)
    {
        try { await SubFeatureIndexAsync(featureId); return true; }
        catch (CenturionException ex) when (ex.Message.StartsWith("Headset has no feature")) { return false; }
    }

    /// <summary>Band centre frequencies of the onboard EQ, as the headset reports them.</summary>
    public async Task<int[]> GetEqFrequenciesAsync() =>
        OnboardEq.ParseFrequencies(await HeadsetAsync(CenturionFeature.OnboardEq, 1, [OnboardEq.ReadLive]));

    public async Task<HeadsetBattery> GetBatteryAsync()
    {
        var d = await HeadsetAsync(CenturionFeature.BatterySoc, 0, []);
        return ParseBattery(d);
    }

    public async Task<int> GetSleepMinutesAsync() => (await HeadsetAsync(CenturionFeature.AutoSleep, 0, []))[0];

    public Task SetSleepMinutesAsync(int minutes)
    {
        if (minutes is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(minutes));
        return HeadsetAsync(CenturionFeature.AutoSleep, 1, [(byte)minutes]);
    }

    /// <summary>Sidetone 0..100. The PRO X 2 replies in the 3-byte v1 layout [mic][?][level].</summary>
    public async Task<int> GetSidetoneAsync() => (await HeadsetAsync(CenturionFeature.Sidetone, 0, []))[^1];

    public Task SetSidetoneAsync(int level)
    {
        if (level is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(level));
        return HeadsetAsync(CenturionFeature.Sidetone, 1, [0x01, (byte)level]);
    }

    public async Task<bool> GetNoiseReductionAsync() => (await HeadsetAsync(CenturionFeature.MicSnr, 0, []))[0] != 0;

    public Task SetNoiseReductionAsync(bool enabled) =>
        HeadsetAsync(CenturionFeature.MicSnr, 1, [(byte)(enabled ? 1 : 0)]);

    public async Task<int[]> GetEqAsync(bool stored = false) =>
        OnboardEq.ParseGains(await HeadsetAsync(CenturionFeature.OnboardEq, 1, [stored ? OnboardEq.ReadStored : OnboardEq.ReadLive]));

    public Task SetEqAsync(IReadOnlyList<int> gains, bool saveToOnboard) =>
        HeadsetAsync(CenturionFeature.OnboardEq, 2, OnboardEq.BuildSetPayload(gains, saveToOnboard));

    public async Task<string> GetFirmwareAsync()
    {
        var d = await HeadsetAsync(CenturionFeature.DeviceInfo, 1, []);
        return $"{d[0]}.{d[1]}.{d[3]}";
    }

    // ---- request plumbing ----

    sealed class Pending(Func<byte[], byte[]?> match)
    {
        public Func<byte[], byte[]?> Match { get; } = match;
        public TaskCompletionSource<byte[]> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    async Task<byte[]> HeadsetAsync(ushort featureId, byte function, byte[] parameters)
    {
        byte index = await SubFeatureIndexAsync(featureId);
        return await BridgeAsync(index, function, parameters);
    }

    async Task<byte> SubFeatureIndexAsync(ushort featureId)
    {
        lock (_subFeatures)
            if (_subFeatures.TryGetValue(featureId, out var cached)) return cached;
        var r = await BridgeAsync(0x00, 0, [(byte)(featureId >> 8), (byte)featureId]);
        if (r.Length == 0 || r[0] == 0) throw new CenturionException($"Headset has no feature 0x{featureId:X4}");
        lock (_subFeatures) _subFeatures[featureId] = r[0];
        return r[0];
    }

    Task<byte[]> DirectAsync(byte featureIndex, byte function, byte[] parameters)
    {
        byte fnSw = (byte)((function << 4) | SoftwareId);
        return SendAsync([CenturionFrames.Direct(featureIndex, function, SoftwareId, parameters)], p =>
        {
            if (p.Length >= 4 && p[0] == 0xFF && p[1] == featureIndex && p[2] == fnSw)
                throw new CenturionException($"Dongle error {p[3]} for feature {featureIndex} fn {function}");
            return p.Length >= 2 && p[0] == featureIndex && p[1] == fnSw ? p[2..] : null;
        });
    }

    Task<byte[]> BridgeAsync(byte subIndex, byte function, byte[] parameters)
    {
        byte fnSw = (byte)((function << 4) | SoftwareId);
        var frames = CenturionFrames.Bridged(_bridgeIndex, subIndex, function, SoftwareId, parameters);
        return SendAsync(frames, p =>
        {
            if (p.Length >= 4 && p[0] == 0xFF && p[1] == _bridgeIndex)
                throw new CenturionException("Headset is off or not connected");
            if (p.Length < 7 || p[0] != _bridgeIndex || p[1] != 0x10 || p[4] != 0x00) return null;
            if (p[5] == 0xFF && p[6] == subIndex && p.Length >= 9 && p[7] == fnSw)
                throw new CenturionException($"Headset error {p[8]} for feature index {subIndex} fn {function}");
            return p[5] == subIndex && p[6] == fnSw ? p[7..] : null;
        });
    }

    async Task<byte[]> SendAsync(IReadOnlyList<byte[]> frames, Func<byte[], byte[]?> match)
    {
        await _gate.WaitAsync();
        try
        {
            var pending = new Pending(match);
            lock (_pendingLock) _pending = pending;
            foreach (var f in frames) _channel.Write(f);
            var done = await Task.WhenAny(pending.Result.Task, Task.Delay(RequestTimeout));
            if (done != pending.Result.Task) throw new CenturionException("Headset did not answer (asleep or off?)");
            return await pending.Result.Task;
        }
        finally
        {
            lock (_pendingLock) _pending = null;
            _gate.Release();
        }
    }

    void OnFrame(byte[] frame)
    {
        var p = CenturionFrames.Payload(frame);
        if (p is null) return;

        Pending? pending;
        lock (_pendingLock) pending = _pending;
        if (pending is not null)
        {
            try
            {
                var result = pending.Match(p);
                if (result is not null) { pending.Result.TrySetResult(result); return; }
            }
            catch (Exception ex) { pending.Result.TrySetException(ex); return; }
        }
        DispatchEvent(p);
    }

    void DispatchEvent(byte[] p)
    {
        if (_bridgeIndex == 0 || p.Length < 4 || p[0] != _bridgeIndex) return;

        // Bridge ConnectionStateChanged: [bridge][0x00][0x00][connected]
        if (p[1] == 0x00)
        {
            HeadsetConnected = p[3] != 0;
            if (!HeadsetConnected) lock (_subFeatures) _subFeatures.Clear();
            ConnectionChanged?.Invoke(HeadsetConnected);
            return;
        }

        // Headset MessageEvent with software id 0: [bridge][0x10][len][len][0x00][subIdx][fn<<4|0][data]
        if (p.Length < 8 || p[1] != 0x10 || p[4] != 0x00 || (p[6] & 0x0F) != 0) return;
        ushort? feature = null;
        lock (_subFeatures)
            foreach (var (id, idx) in _subFeatures)
                if (idx == p[5]) feature = id;
        var data = p[7..];
        switch (feature)
        {
            case CenturionFeature.BatterySoc when data.Length >= 3:
                BatteryChanged?.Invoke(ParseBattery(data));
                break;
            case CenturionFeature.Sidetone when data.Length >= 3:
                SidetoneChanged?.Invoke(data[2]);
                break;
            case CenturionFeature.MicSnr:
                NoiseReductionChanged?.Invoke(data[0] != 0);
                break;
        }
    }

    static HeadsetBattery ParseBattery(byte[] d) => new(d[0], d.Length >= 3 && d[2] is 1 or 2);

    public void Dispose()
    {
        _channel.FrameReceived -= OnFrame;
        _channel.Dispose();
    }
}
