using Hub.Core.Hid;

namespace Hub.Core.Razer;

public sealed record DpiStage(int X, int Y);

public sealed record DpiStages(int ActiveStage, IReadOnlyList<DpiStage> Stages);

public sealed record BatteryState(int Percent, bool Charging);

public sealed record LiftOff(int LiftOffDistance, int LandingDistance);

public sealed record MouseCapabilities(bool DpiStages, bool Polling, bool Battery, bool PowerSaving, bool LiftOff,
    bool DynamicSensitivity, bool Rotation, IReadOnlyList<byte> Buttons);

public enum DynamicSensitivityMode : byte { Classic = 0, Natural = 1, Jump = 2, Custom = 3 }

public enum DeviceMode : byte { Normal = 0, Bootloader = 1, Testing = 2, Driver = 3, Pairing = 5, Standby = 6 }

/// <summary>How a mouse reports its polling rate.</summary>
public enum PollingScheme
{
    /// <summary>00,C0 / 00,40 [profile, code]: HyperPolling dongles (Viper V3 Pro wireless).</summary>
    HyperPolling,
    /// <summary>00,8E / 00,0E [profile, code]: profile-based (Viper V3 Pro wired).</summary>
    Profile,
    /// <summary>00,85 / 00,05 [code]: the classic Razer command most mice support.</summary>
    Legacy,
}

/// <summary>
/// Razer mouse commands (docs/protocols.md; verified on a Viper V3 Pro). Other Razer mice share the
/// packet format and most commands; per-model differences are the transaction id and polling scheme.
/// GET ids are the SET id with bit 7 set. All multi-byte values are big-endian.
/// </summary>
public sealed class RazerMouse(IFeatureChannel channel, bool viaDongle, byte transactionId = RazerPacket.DefaultTransactionId,
    PollingScheme? polling = null) : IDisposable
{
    public const ushort VendorId = 0x1532;
    public const ushort WiredProductId = 0x00C0;
    public const ushort DongleProductId = 0x00C1;

    /// <summary>Transaction ids seen across Razer mice, tried in order when a model is unknown.</summary>
    public static readonly byte[] KnownTransactionIds = [0x1F, 0x3F, 0xFF];

    /// <summary>HyperPolling codes (00,40 / 00,C0), used over the dongle.</summary>
    public static readonly IReadOnlyDictionary<int, byte> HyperPollingCodes = new Dictionary<int, byte>
    {
        [8000] = 1, [4000] = 2, [2000] = 4, [1000] = 8, [500] = 16, [250] = 32, [125] = 64,
    };

    /// <summary>Profile polling codes (00,0E / 00,8E), used over the cable.</summary>
    public static readonly IReadOnlyDictionary<int, byte> WiredPollingCodes = new Dictionary<int, byte>
    {
        [1000] = 1, [500] = 2, [125] = 8,
    };

    public byte TransactionId { get; } = transactionId;

    /// <summary>The Viper V3 Pro's split unless the model says otherwise: HyperPolling over the dongle, profile over the cable.</summary>
    public PollingScheme Polling { get; } = polling ?? (viaDongle ? PollingScheme.HyperPolling : PollingScheme.Profile);

    IReadOnlyDictionary<int, byte> PollingCodes => Polling == PollingScheme.HyperPolling ? HyperPollingCodes : WiredPollingCodes;

    const byte OnboardProfile = 1;
    const byte NormalLayer = 0; // 0 = normal layer, 1 = Hypershift (secondary) layer
    readonly object _lock = new();

    public bool ViaDongle { get; } = viaDongle;

    public IReadOnlyCollection<int> SupportedPollingRates => PollingCodes.Keys.OrderBy(r => r).ToArray();

    // ---- device ----

    public string GetFirmwareVersion()
    {
        var d = Get(0x00, 0x87, 4);
        return $"{d[0]}.{d[1]}.{d[2]}.{d[3]}";
    }

    public string GetSerial() =>
        System.Text.Encoding.ASCII.GetString(Get(0x00, 0x82, 22)).TrimEnd('\0');

    public DeviceMode GetDeviceMode() => (DeviceMode)Get(0x00, 0x84, 2)[0];

    public void SetDeviceMode(DeviceMode mode) => Set(0x00, 0x04, 2, (byte)mode, 0x00);

    public bool IsWirelessConnected() => Get(0x00, 0xB8, 1)[0] != 0;

    // ---- polling ----

    public int GetPollingRate()
    {
        byte code = Polling switch
        {
            PollingScheme.HyperPolling => Get(0x00, 0xC0, 2, OnboardProfile)[1],
            PollingScheme.Profile => Get(0x00, 0x8E, 2, OnboardProfile)[1],
            _ => Get(0x00, 0x85, 1)[0],
        };
        foreach (var (rate, c) in PollingCodes)
            if (c == code) return rate;
        throw new RazerException($"Unknown polling code {code}");
    }

    public void SetPollingRate(int hz)
    {
        if (!PollingCodes.TryGetValue(hz, out var code))
            throw new ArgumentOutOfRangeException(nameof(hz), $"{hz} Hz is not supported on this connection");
        switch (Polling)
        {
            case PollingScheme.HyperPolling: Set(0x00, 0x40, 2, OnboardProfile, code); break;
            case PollingScheme.Profile: Set(0x00, 0x0E, 2, OnboardProfile, code); break;
            default: Set(0x00, 0x05, 1, code); break;
        }
    }

    // ---- DPI ----

    public DpiStage GetDpi()
    {
        var d = Get(0x04, 0x85, 7, 0x00);
        return new DpiStage(U16(d, 1), U16(d, 3));
    }

    public void SetDpi(DpiStage dpi)
    {
        CheckDpi(dpi);
        Set(0x04, 0x05, 7, 0x00, Hi(dpi.X), Lo(dpi.X), Hi(dpi.Y), Lo(dpi.Y), 0, 0);
    }

    public DpiStages GetDpiStages()
    {
        var d = Get(0x04, 0x86, 80, OnboardProfile);
        int active = d[1], count = d[2];
        var stages = new List<DpiStage>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 3 + i * 7; // [stageId, xH, xL, yH, yL, 0, 0]
            stages.Add(new DpiStage(U16(d, o + 1), U16(d, o + 3)));
        }
        return new DpiStages(active, stages);
    }

    public void SetDpiStages(DpiStages stages)
    {
        int n = stages.Stages.Count;
        if (n is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(stages), "1 to 5 DPI stages");
        if (stages.ActiveStage < 1 || stages.ActiveStage > n) throw new ArgumentOutOfRangeException(nameof(stages), "active stage out of range");

        var args = new List<byte> { OnboardProfile, (byte)stages.ActiveStage, (byte)n };
        for (int i = 0; i < n; i++)
        {
            var s = stages.Stages[i];
            CheckDpi(s);
            args.AddRange([(byte)i, Hi(s.X), Lo(s.X), Hi(s.Y), Lo(s.Y), 0, 0]);
        }
        Set(0x04, 0x06, (byte)(7 * n + 3), [.. args]);
    }

    static void CheckDpi(DpiStage s)
    {
        if (s.X is < 100 or > 35000 || s.Y is < 100 or > 35000)
            throw new ArgumentOutOfRangeException(nameof(s), "DPI must be 100..35000");
    }

    // ---- power ----

    public BatteryState GetBattery()
    {
        int raw = Get(0x07, 0x80, 2)[1];
        bool charging = Get(0x07, 0x84, 2)[1] == 1;
        return new BatteryState(raw * 100 / 255, charging);
    }

    public int GetLowBatteryThresholdPercent() => (int)Math.Round(Get(0x07, 0x81, 1)[0] * 100 / 255.0);

    public void SetLowBatteryThresholdPercent(int percent)
    {
        if (percent is < 5 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        Set(0x07, 0x01, 1, (byte)Math.Ceiling(percent / 100.0 * 255));
    }

    public TimeSpan GetIdleTimeout() => TimeSpan.FromSeconds(U16(Get(0x07, 0x83, 2), 0));

    public void SetIdleTimeout(TimeSpan timeout)
    {
        int seconds = (int)timeout.TotalSeconds;
        if (seconds is < 60 or > 900) throw new ArgumentOutOfRangeException(nameof(timeout), "1..15 minutes");
        Set(0x07, 0x03, 2, Hi(seconds), Lo(seconds));
    }

    // ---- sensor ----

    /// <summary>Asymmetric lift-off/landing distances (reply layout [0, 4, 01, lift-1, land-1]).</summary>
    public LiftOff GetLiftOff()
    {
        var d = Get(0x0B, 0x85, 10, 0x00, 0x04);
        return new LiftOff(d[3] + 1, d[4] + 1);
    }

    public void SetAsymmetricLiftOff(LiftOff lift)
    {
        if (lift.LiftOffDistance is < 2 or > 26 || lift.LandingDistance < 1 || lift.LandingDistance >= lift.LiftOffDistance)
            throw new ArgumentOutOfRangeException(nameof(lift), "landing must be below lift-off");
        Set(0x0B, 0x03, 3, 0x00, 0x04, 0x01);
        Set(0x0B, 0x0B, 4, 0x00, 0x04, 0x04, 0x00);
        Set(0x0B, 0x05, 10, 0x00, 0x04, 0x01, (byte)(lift.LiftOffDistance - 1), (byte)(lift.LandingDistance - 1));
    }

    /// <summary>Lift mode from 0B,8B: [0, 4, mode, height]. Mode 4 = asymmetric (preset hysteresis), 1 = symmetric smart tracking.</summary>
    public (bool Asymmetric, int TrackingDistance) GetLiftMode()
    {
        var d = Get(0x0B, 0x8B, 4, 0x00, 0x04);
        return (d[2] == 4, d[3] + 1);
    }

    /// <summary>Symmetric "smart tracking": 0B,03 [00 04 01] then 0B,0B [00 04 01 distance-1].</summary>
    public void SetSymmetricLiftOff(int trackingDistance)
    {
        if (trackingDistance is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(trackingDistance), "1..3");
        Set(0x0B, 0x03, 3, 0x00, 0x04, 0x01);
        Set(0x0B, 0x0B, 4, 0x00, 0x04, 0x01, (byte)(trackingDistance - 1));
    }

    public bool GetDynamicSensitivityEnabled() => Get(0x0B, 0x90, 2, OnboardProfile)[1] != 0;

    public void SetDynamicSensitivityEnabled(bool enabled) => Set(0x0B, 0x10, 2, OnboardProfile, (byte)(enabled ? 1 : 0));

    public DynamicSensitivityMode GetDynamicSensitivityMode() => (DynamicSensitivityMode)Get(0x0B, 0x91, 2, OnboardProfile)[1];

    public void SetDynamicSensitivityMode(DynamicSensitivityMode mode) => Set(0x0B, 0x11, 2, OnboardProfile, (byte)mode);

    /// <summary>Reads the 250-sample curve in 8-sample chunks: 0B,93 [P, mode, 250, offset, n] → same header + n float32 LE.</summary>
    public float[] GetCurveSamples(DynamicSensitivityMode mode)
    {
        var samples = new float[SensitivityCurve.SampleCount];
        for (int offset = 0; offset < samples.Length; offset += 8)
        {
            int n = Math.Min(8, samples.Length - offset);
            var d = Get(0x0B, 0x93, 37, OnboardProfile, (byte)mode, SensitivityCurve.SampleCount, (byte)offset, (byte)n);
            for (int i = 0; i < n; i++) samples[offset + i] = BitConverter.ToSingle(d, 5 + i * 4);
        }
        return samples;
    }

    /// <summary>Writes a custom curve (32 chunks, 15 ms apart so the firmware keeps up) and selects Custom mode.</summary>
    public void SetCustomCurve(IReadOnlyList<CurvePoint> points)
    {
        var samples = SensitivityCurve.Sample(points);
        SetDynamicSensitivityMode(DynamicSensitivityMode.Custom);
        for (int offset = 0; offset < samples.Length; offset += 8)
        {
            int n = Math.Min(8, samples.Length - offset);
            var args = new byte[5 + n * 4];
            args[0] = OnboardProfile; args[1] = (byte)DynamicSensitivityMode.Custom; args[2] = SensitivityCurve.SampleCount;
            args[3] = (byte)offset; args[4] = (byte)n;
            for (int i = 0; i < n; i++) BitConverter.GetBytes((float)samples[offset + i]).CopyTo(args, 5 + i * 4);
            Set(0x0B, 0x13, 37, args);
            Thread.Sleep(15);
        }
    }

    /// <summary>Sensor rotation: 0B,94/14 [P, enabled, angle]. Angle is a signed byte, -44..44 degrees.</summary>
    public (bool Enabled, int Angle) GetRotation()
    {
        var d = Get(0x0B, 0x94, 3, OnboardProfile);
        return (d[1] != 0, (sbyte)d[2]);
    }

    public void SetRotation(bool enabled, int angle)
    {
        if (angle is < -44 or > 44) throw new ArgumentOutOfRangeException(nameof(angle), "-44..44 degrees");
        Set(0x0B, 0x14, 3, OnboardProfile, (byte)(enabled ? 1 : 0), (byte)(sbyte)angle);
    }

    // ---- buttons ----

    public ButtonAssignment GetButton(byte buttonId)
    {
        var d = Get(0x02, 0x8C, 10, OnboardProfile, buttonId, NormalLayer);
        int len = d[4];
        return new ButtonAssignment(buttonId, (ButtonFunction)d[3], d.AsSpan(5, Math.Min(len, 5)).ToArray());
    }

    public void SetButton(ButtonAssignment a)
    {
        if (a.Data.Length > 5) throw new ArgumentException("at most 5 function data bytes", nameof(a));
        var args = new byte[5 + a.Data.Length];
        args[0] = OnboardProfile; args[1] = a.ButtonId; args[2] = NormalLayer;
        args[3] = (byte)a.Function; args[4] = (byte)a.Data.Length;
        a.Data.CopyTo(args, 5);
        Set(0x02, 0x0C, 10, args);
    }

    // ---- discovery ----

    /// <summary>
    /// Which feature groups this mouse answers, found with read-only GETs. Every group the Viper V3 Pro has
    /// answers on it; on other mice anything that isn't supported is simply hidden.
    /// </summary>
    public MouseCapabilities Probe(IEnumerable<byte> buttonIds)
    {
        bool Try(Action read)
        {
            try { read(); return true; }
            catch (RazerException ex) when (ex.Status != RazerStatus.Timeout || !ViaDongle) { return false; }
        }
        return new MouseCapabilities(
            DpiStages: Try(() => GetDpiStages()),
            Polling: Try(() => GetPollingRate()),
            Battery: Try(() => GetBattery()),
            PowerSaving: Try(() => { GetIdleTimeout(); GetLowBatteryThresholdPercent(); }),
            LiftOff: Try(() => { GetLiftMode(); GetLiftOff(); }),
            DynamicSensitivity: Try(() => { GetDynamicSensitivityEnabled(); GetDynamicSensitivityMode(); }),
            Rotation: Try(() => GetRotation()),
            Buttons: buttonIds.Where(b => Try(() => GetButton(b))).ToArray());
    }

    /// <summary>For an unknown model: the transaction id and polling scheme it answers to, or null if it doesn't respond.</summary>
    public static (byte TransactionId, PollingScheme Polling)? Identify(IFeatureChannel channel, bool viaDongle)
    {
        foreach (var tid in KnownTransactionIds)
        {
            var probe = new RazerMouse(channel, viaDongle, tid, PollingScheme.Legacy);
            try { probe.GetFirmwareVersion(); }
            catch (RazerException) { continue; }
            foreach (var scheme in new[] { PollingScheme.HyperPolling, PollingScheme.Profile, PollingScheme.Legacy })
            {
                try { new RazerMouse(channel, viaDongle, tid, scheme).GetPollingRate(); return (tid, scheme); }
                catch (RazerException) { }
            }
            return (tid, PollingScheme.Legacy);
        }
        return null;
    }

    // ---- transport ----

    byte[] Get(byte cls, byte id, byte size, params byte[] args) => Transact(cls, id, size, args);

    void Set(byte cls, byte id, byte size, params byte[] args) => Transact(cls, id, size, args);

    byte[] Transact(byte cls, byte id, byte size, byte[] args)
    {
        lock (_lock)
        {
            var report = RazerPacket.BuildReport(cls, id, size, args, TransactionId);
            int sleepMs = ViaDongle ? 25 : 5;
            RazerStatus last = RazerStatus.New;
            for (int send = 0; send < 5; send++)
            {
                channel.SetFeature(report);
                for (int read = 0; read < 10; read++)
                {
                    Thread.Sleep(sleepMs);
                    var resp = channel.GetFeature(RazerPacket.ReportLength);
                    var p = resp.AsSpan(1);
                    last = (RazerStatus)p[0];
                    if (last == RazerStatus.Busy) continue;
                    if (p[1] != report[2] || p[6] != cls || p[7] != id) break; // stale reply: resend
                    switch (last)
                    {
                        case RazerStatus.Success:
                            return p.Slice(8, 80).ToArray();
                        case RazerStatus.NotSupported:
                            throw new RazerException($"Command {cls:X2},{id:X2} not supported", last);
                        case RazerStatus.Timeout when ViaDongle:
                            throw new RazerException("Mouse is asleep or out of range", last);
                    }
                    break;
                }
            }
            throw new RazerException($"Command {cls:X2},{id:X2} failed ({last})", last);
        }
    }

    static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    static byte Hi(int v) => (byte)(v >> 8);
    static byte Lo(int v) => (byte)v;

    public void Dispose() => channel.Dispose();
}
