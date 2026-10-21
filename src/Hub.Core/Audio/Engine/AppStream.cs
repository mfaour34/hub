namespace Hub.Core.Audio.Engine;

/// <summary>
/// One app's captured audio on its way to the output device. Capture and output run on different clocks (the app
/// plays to a silent device, Hub plays to the headset), so frames are read back through a fractional resampler
/// whose ratio a PI controller nudges to keep the backlog at <see cref="TargetFrames"/>: the backlog is the stream's
/// added latency, and keeping it constant is what stops clicks from slowly building under- or overruns.
/// </summary>
public sealed class AppStream
{
    // 32-tap Kaiser-windowed-sinc fractional delay, 128 phases with linear interpolation between them. Ratios stay
    // within ±0.5 % of 1, so no anti-alias cut is needed: at 48 kHz the kernel is flat to ±0.01 dB up to 20 kHz
    // for every fractional position.
    const int Taps = 32, Half = Taps / 2, Phases = 128;
    static readonly float[] Kernel = BuildKernel();

    readonly float[] _l, _r;
    int _count;                 // frames held, from index 0
    double _pos;                // read position (frames) of the next output frame
    bool _playing;
    double _ratio = 1, _integral, _avgFill = -1;

    public AppStream(int sampleRate, double targetMs = 10)
    {
        SampleRate = sampleRate;
        TargetFrames = (int)(sampleRate * targetMs / 1000);
        _l = new float[sampleRate * 2];
        _r = new float[sampleRate * 2];
        Reset();
    }

    public int SampleRate { get; }
    public int TargetFrames { get; }
    public double Ratio => _ratio;
    public int Underruns { get; private set; }
    public int Overruns { get; private set; }
    public bool Playing => _playing;

    /// <summary>Frames ready to be read ahead of the read position.</summary>
    public int Backlog => Math.Max(0, _count - Half - (int)_pos);

    void Reset()
    {
        // history of silence for the kernel's left half
        Array.Clear(_l); Array.Clear(_r);
        _count = Half;
        _pos = Half;
        _playing = false;
    }

    /// <summary>Appends interleaved stereo frames (capture thread order is preserved by the caller).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        if (_count + frames > _l.Length) Compact();
        if (_count + frames > _l.Length)
        {
            // stalled far beyond any sane backlog: start over from the newest audio
            Overruns++;
            Reset();
            if (frames > _l.Length - _count) interleaved = interleaved[^((_l.Length - _count) * 2)..];
            frames = interleaved.Length / 2;
        }
        for (int i = 0; i < frames; i++)
        {
            _l[_count + i] = interleaved[2 * i];
            _r[_count + i] = interleaved[2 * i + 1];
        }
        _count += frames;
    }

    /// <summary>Writes <paramref name="left"/>.Length resampled frames (adds nothing while buffering).</summary>
    public void Read(Span<float> left, Span<float> right)
    {
        int frames = left.Length;
        if (!_playing)
        {
            if (Backlog < TargetFrames) { left.Clear(); right.Clear(); return; }
            // start exactly on target (counted after this read): whatever arrived in the first burst would
            // otherwise be latency to work off
            _pos += Math.Max(0, Backlog - TargetFrames - frames);
            _avgFill = TargetFrames;
            _integral = 0;
            _playing = true;
        }

        for (int n = 0; n < left.Length; n++)
        {
            int i = (int)_pos;
            if (i + Half >= _count)
            {
                // ran dry: pad with silence and rebuffer before playing again
                left[n..].Clear(); right[n..].Clear();
                Underruns++;
                _playing = false;
                _integral = 0;
                break;
            }
            double frac = _pos - i;
            double ph = frac * Phases;
            int p = (int)ph;
            float t = (float)(ph - p);
            int k0 = p * Taps, k1 = k0 + Taps;
            float sl = 0, sr = 0;
            int baseIdx = i - Half + 1;
            for (int k = 0; k < Taps; k++)
            {
                float w = Kernel[k0 + k] + t * (Kernel[k1 + k] - Kernel[k0 + k]);
                sl += _l[baseIdx + k] * w;
                sr += _r[baseIdx + k] * w;
            }
            left[n] = sl; right[n] = sr;
            _pos += _ratio;
        }

        if (_playing) Steer(frames);
        if (_pos > _l.Length / 2) Compact();
    }

    void Steer(int frames)
    {
        // average the backlog over ~0.5 s; packets arrive in bursts, the average is what drifts
        double fill = Backlog;
        double alpha = Math.Min(1, frames / (0.5 * SampleRate));
        _avgFill = _avgFill < 0 ? fill : _avgFill + alpha * (fill - _avgFill);
        double err = (_avgFill - TargetFrames) / TargetFrames;
        double dt = frames / (double)SampleRate;
        _integral = Math.Clamp(_integral + err * dt, -5, 5);
        _ratio = Math.Clamp(1 + 0.0015 * err + 0.0004 * _integral, 0.995, 1.005);

        // far behind (e.g. after the app's audio thread stalled): skip ahead instead of drifting back for minutes
        if (fill > TargetFrames * 6 + SampleRate / 20)
        {
            _pos += fill - TargetFrames;
            _avgFill = TargetFrames;
            Overruns++;
        }
    }

    void Compact()
    {
        int drop = (int)_pos - Half;
        if (drop <= 0) return;
        Array.Copy(_l, drop, _l, 0, _count - drop);
        Array.Copy(_r, drop, _r, 0, _count - drop);
        _count -= drop;
        _pos -= drop;
    }

    static float[] BuildKernel()
    {
        // tap k of phase p weights input sample (i - Half + 1 + k) for output position i + p/Phases
        var table = new float[(Phases + 1) * Taps];
        const double beta = 7.0;
        for (int p = 0; p <= Phases; p++)
        {
            double frac = p / (double)Phases, sum = 0;
            var row = new double[Taps];
            for (int k = 0; k < Taps; k++)
            {
                double x = k - Half + 1 - frac; // distance from the output position, in input samples
                double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double u = x / Half;
                double w = Math.Abs(u) >= 1 ? 0 : Bessel0(beta * Math.Sqrt(1 - u * u)) / Bessel0(beta);
                row[k] = sinc * w;
                sum += row[k];
            }
            for (int k = 0; k < Taps; k++) table[p * Taps + k] = (float)(row[k] / sum);
        }
        return table;
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
