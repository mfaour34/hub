namespace Hub.Core.Audio.Engine;

/// <summary>
/// A channel's EQ as Hub's engine runs it: preamp, then the preset's biquads in order (the same RBJ coefficients as
/// <see cref="Biquad"/>, so the response is what Equalizer APO produced). Stereo, double-precision transposed
/// direct form II.
/// </summary>
public sealed class EqChain
{
    readonly double[] _b0, _b1, _b2, _a1, _a2;
    readonly double[] _zL1, _zL2, _zR1, _zR2;
    readonly float _preamp;

    public EqChain(EqPresetData preset, double sampleRate, double preampDb)
    {
        var sections = preset.Filters.Where(f => f.Enabled && f.Frequency < sampleRate / 2).Select(f => Biquad.For(f, sampleRate)).ToArray();
        int n = sections.Length;
        _b0 = new double[n]; _b1 = new double[n]; _b2 = new double[n]; _a1 = new double[n]; _a2 = new double[n];
        _zL1 = new double[n]; _zL2 = new double[n]; _zR1 = new double[n]; _zR2 = new double[n];
        for (int i = 0; i < n; i++)
        {
            var s = sections[i];
            _b0[i] = s.B0 / s.A0; _b1[i] = s.B1 / s.A0; _b2[i] = s.B2 / s.A0; _a1[i] = s.A1 / s.A0; _a2[i] = s.A2 / s.A0;
        }
        _preamp = (float)Math.Pow(10, preampDb / 20);
        PreampDb = preampDb;
    }

    public double PreampDb { get; }

    /// <summary>
    /// The preamp Hub used in Equalizer APO's config: the preset's own gain in front of a limiter, otherwise lowered
    /// by the curve's peak boost when auto headroom is on (see <see cref="EqPresetData.ToEqualizerApo"/>).
    /// </summary>
    public static double Preamp(EqPresetData preset, bool limiter, bool autoHeadroom, double sampleRate) =>
        !limiter && autoHeadroom ? preset.GainDb - preset.PeakBoostDb(sampleRate) : preset.GainDb;

    public void Process(Span<float> left, Span<float> right)
    {
        Run(left, _zL1, _zL2);
        Run(right, _zR1, _zR2);
    }

    void Run(Span<float> x, double[] z1, double[] z2)
    {
        int n = _b0.Length;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i] * _preamp;
            for (int s = 0; s < n; s++)
            {
                double y = _b0[s] * v + z1[s];
                z1[s] = _b1[s] * v - _a1[s] * y + z2[s];
                z2[s] = _b2[s] * v - _a2[s] * y;
                v = y;
            }
            x[i] = (float)v;
        }
        // flush denormals left behind by silence
        for (int s = 0; s < n; s++)
        {
            if (Math.Abs(z1[s]) < 1e-30) z1[s] = 0;
            if (Math.Abs(z2[s]) < 1e-30) z2[s] = 0;
        }
    }
}
