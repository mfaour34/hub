namespace Hub.Core.Audio.Engine;

/// <summary>
/// 2× up- and down-sampling around a channel's 96 kHz EQ and limiter (apps and
/// the headphones run at 48 kHz). A 63-tap Kaiser halfband: flat to 20 kHz (±0.01 dB), −6 dB at 24 kHz, and
/// images/aliases above 28 kHz down by more than 80 dB. Doing this here instead of letting Windows convert the
/// streams is what keeps the top octave intact.
/// </summary>
public sealed class Oversampler
{
    const int Taps = 63, Center = Taps / 2;
    static readonly double[] H = Design();
    // the halfband's non-zero taps only (every second tap is exactly zero): 33 multiplies per output, no branches
    static readonly int[] NzIndex = Enumerable.Range(0, Taps).Where(k => H[k] != 0).ToArray();
    static readonly double[] NzCoef = NzIndex.Select(k => H[k]).ToArray();

    // history at 48 kHz (upsampler) and 96 kHz (downsampler), per channel, as double-length mirrors for contiguous reads
    readonly double[] _upL = new double[2 * Taps], _upR = new double[2 * Taps];
    readonly double[] _dnL = new double[2 * Taps], _dnR = new double[2 * Taps];
    int _up, _dn;

    /// <summary>Group delay of an up+down round trip, in 48 kHz frames.</summary>
    public const int LatencyFrames = Center; // 31.5 samples at 96 kHz each way ≈ 31 samples at 48 kHz in total

    /// <summary>48 kHz in → 96 kHz out (<paramref name="outL"/> is twice as long as <paramref name="inL"/>).</summary>
    public void Up(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, Span<float> outL, Span<float> outR)
    {
        for (int n = 0; n < inL.Length; n++)
        {
            for (int phase = 0; phase < 2; phase++)
            {
                // zero-stuffed input: x[2n] = in[n], x[2n+1] = 0
                Push(_upL, _upR, ref _up, phase == 0 ? inL[n] : 0, phase == 0 ? inR[n] : 0);
                double l = 0, r = 0;
                int start = _up; // oldest sample of the window; h is symmetric, so window order = tap order
                for (int j = 0; j < NzIndex.Length; j++)
                {
                    int k = start + NzIndex[j];
                    l += NzCoef[j] * _upL[k]; r += NzCoef[j] * _upR[k];
                }
                outL[2 * n + phase] = (float)(2 * l);
                outR[2 * n + phase] = (float)(2 * r);
            }
        }
    }

    /// <summary>96 kHz in → 48 kHz out (<paramref name="inL"/> is twice as long as <paramref name="outL"/>).</summary>
    public void Down(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR, Span<float> outL, Span<float> outR)
    {
        for (int n = 0; n < outL.Length; n++)
        {
            Push(_dnL, _dnR, ref _dn, inL[2 * n], inR[2 * n]);
            Push(_dnL, _dnR, ref _dn, inL[2 * n + 1], inR[2 * n + 1]);
            double l = 0, r = 0;
            int start = _dn;
            for (int j = 0; j < NzIndex.Length; j++)
            {
                int k = start + NzIndex[j];
                l += NzCoef[j] * _dnL[k]; r += NzCoef[j] * _dnR[k];
            }
            outL[n] = (float)l; outR[n] = (float)r;
        }
    }

    static void Push(double[] l, double[] r, ref int pos, double a, double b)
    {
        // ring of Taps samples stored twice so that [pos, pos + Taps) is always the window, oldest first
        l[pos] = l[pos + Taps] = a;
        r[pos] = r[pos + Taps] = b;
        pos = (pos + 1) % Taps;
    }

    static double[] Design()
    {
        // windowed sinc at a quarter of the 96 kHz rate; every second tap is exactly zero (halfband)
        var h = new double[Taps];
        const double beta = 8.0;
        double sum = 0;
        for (int k = 0; k < Taps; k++)
        {
            int m = k - Center;
            double sinc = m == 0 ? 0.5 : Math.Sin(Math.PI * m / 2) / (Math.PI * m);
            double u = m / (double)Center;
            double w = Bessel0(beta * Math.Sqrt(Math.Max(0, 1 - u * u))) / Bessel0(beta);
            h[k] = m != 0 && m % 2 == 0 ? 0 : sinc * w;
            sum += h[k];
        }
        for (int k = 0; k < Taps; k++) h[k] /= sum; // unity gain at DC
        return h;
    }

    static double Bessel0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 50; k++)
        {
            term *= (x / (2 * k)) * (x / (2 * k));
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }
}
