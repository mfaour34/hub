namespace Hub.Core.Logitech;

/// <summary>
/// 0x0636 OnboardEQ setEQParameters payload. Ported from HeadsetControl (https://github.com/Sapd/HeadsetControl,
/// GPL-3.0) and verified on a PRO X 2 LIGHTSPEED.
/// 5 fixed peaking bands; the host supplies biquad coefficients for a 48 kHz and a 16 kHz path.
/// </summary>
public static class OnboardEq
{
    public static readonly int[] Frequencies = [80, 240, 750, 2200, 6600];
    public const int MinGain = -12, MaxGain = 12;

    /// <summary>Slot byte flags: 0x00 applies live only, 0x80 also saves to onboard memory.</summary>
    public const byte LiveOnly = 0x00, SaveToOnboard = 0x80;

    /// <summary>fn1 read selector: 0x00 = stored copy, 0x80 = live copy.</summary>
    public const byte ReadStored = 0x00, ReadLive = 0x80;

    /// <summary>Q byte that gives the headset's stock band shape.</summary>
    public const byte DefaultQ = 2;

    public static byte[] BuildSetPayload(IReadOnlyList<int> gains, bool save, byte q = DefaultQ)
    {
        if (gains.Count != Frequencies.Length) throw new ArgumentException("exactly 5 gains", nameof(gains));
        foreach (var g in gains)
            if (g is < MinGain or > MaxGain) throw new ArgumentOutOfRangeException(nameof(gains), "gain must be -12..12 dB");

        var o = new List<byte> { save ? SaveToOnboard : LiveOnly, (byte)gains.Count };
        for (int i = 0; i < gains.Count; i++)
            o.AddRange([(byte)(Frequencies[i] >> 8), (byte)Frequencies[i], (byte)(sbyte)gains[i], q]);
        o.AddRange([0x05, 0x5A, 0xE3, 0x00, 0x03, 0x0E, 0x00, 0x02, 0x00, 0x00, 0x00]);
        AddSection(o, gains, q, 48000, 0x01);
        AddSection(o, gains, q, 16000, 0x02);
        return [.. o];
    }

    /// <summary>Parses an fn1 reply: [flags][n] + n × [freqBE16][gain int8][Q].</summary>
    public static int[] ParseGains(ReadOnlySpan<byte> reply)
    {
        int n = reply[1];
        var gains = new int[n];
        for (int i = 0; i < n; i++) gains[i] = (sbyte)reply[2 + i * 4 + 2];
        return gains;
    }

    /// <summary>Band frequencies from the same fn1 reply.</summary>
    public static int[] ParseFrequencies(ReadOnlySpan<byte> reply)
    {
        int n = reply[1];
        var f = new int[n];
        for (int i = 0; i < n; i++) f[i] = (reply[2 + i * 4] << 8) | reply[3 + i * 4];
        return f;
    }

    /// <summary>
    /// The set payload (and its coefficient tail) was verified only for the PRO X 2's 5-band layout, so Hub writes
    /// EQ only to headsets that report exactly these bands.
    /// </summary>
    public static bool IsSupportedLayout(IReadOnlyList<int> frequencies) => frequencies.SequenceEqual(Frequencies);

    static double[] Peaking(double f, double gainDb, double q, double fs)
    {
        double a = Math.Pow(10, gainDb / 40), w0 = 2 * Math.PI * f / fs, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha / a;
        return [(1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0];
    }

    static void AddFixed(List<byte> o, double value, double scale, long mask)
    {
        long q = (long)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        q = Math.Clamp(q, -(1L << 31), (1L << 31) - 1) & mask;
        ushort hi = (ushort)((q >> 16) & 0xFFFF), lo = (ushort)(q & 0xFFFF);
        o.AddRange([(byte)hi, (byte)(hi >> 8), (byte)lo, (byte)(lo >> 8)]);
    }

    static void AddSection(List<byte> o, IReadOnlyList<int> gains, byte q, double fs, byte type)
    {
        var coeffs = gains.Select((g, i) => Peaking(Frequencies[i], g, Math.Max(0.1, q), fs)).ToList();
        double rescale = Math.Max(1, coeffs.Max(c => Math.Abs(c[0]))) * 1.19;
        int count = gains.Count * 10 + 3;
        o.AddRange([type, 0x00, (byte)count, (byte)(count >> 8), (byte)gains.Count, 0x00]);
        double[] scales = [2147483648.0, 1073741824.0, 2147483648.0, 1073741824.0, 2147483648.0];
        foreach (var c in coeffs)
        {
            double[] n = [c[0] / rescale, c[1] / rescale, c[2] / rescale, c[3], c[4]];
            for (int k = 0; k < 5; k++) AddFixed(o, n[k], scales[k], ~0xFFL);
        }
        AddFixed(o, rescale, 67108864.0, 0xFFFFFF00L);
    }
}
