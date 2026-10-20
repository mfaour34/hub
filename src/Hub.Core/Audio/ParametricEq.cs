using System.Globalization;
using System.Numerics;
using System.Text;

namespace Hub.Core.Audio;

/// <summary>Filter types, all RBJ Audio-EQ-Cookbook biquads like Equalizer APO's.</summary>
public enum FilterType { Peaking, LowShelf, HighShelf, LowPass, HighPass, Notch }

public sealed record EqFilter(FilterType Type, double Frequency, double GainDb, double Q, bool Enabled = true);

/// <summary>A channel EQ: filters plus a gain applied before them. Headroom is added on top when exported.</summary>
/// <param name="Category">"Built-in" for Hub's presets, "Custom" for the user's profiles.</param>
public sealed record EqPresetData(string Name, double GainDb, IReadOnlyList<EqFilter> Filters, string Category = "Built-in")
{
    public static EqPresetData Flat(string name = "Flat") => new(name, 0, []);

    /// <summary>Highest boost of the combined response between 20 Hz and 20 kHz, in dB (0 if it never boosts).</summary>
    public double PeakBoostDb(double sampleRate = 48000)
    {
        var active = Filters.Where(f => f.Enabled && f.Frequency < sampleRate / 2).Select(f => Biquad.For(f, sampleRate)).ToList();
        if (active.Count == 0) return 0;
        double peak = 0;
        for (int i = 0; i <= 600; i++)
        {
            double f = 20 * Math.Pow(1000, i / 600.0); // 20 Hz .. 20 kHz, log spaced
            double db = active.Sum(b => b.MagnitudeDb(f, sampleRate));
            peak = Math.Max(peak, db);
        }
        return peak;
    }

    /// <summary>
    /// Equalizer APO config text. With <paramref name="limiterLibrary"/> the EQ runs at the preset's own gain and the
    /// look-ahead limiter catches anything above full scale. Without it, a preamp keeps
    /// the curve's peak at or below 0 dBFS instead, unless <paramref name="autoHeadroom"/> is off.
    /// </summary>
    public string ToEqualizerApo(string? limiterLibrary = null, bool autoHeadroom = true, double sampleRate = 48000)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# {Name}");
        double preamp = limiterLibrary is null && autoHeadroom ? GainDb - PeakBoostDb(sampleRate) : GainDb;
        sb.AppendLine(string.Format(inv, "Preamp: {0:0.0} dB", preamp));
        foreach (var f in Filters.Where(f => f.Enabled))
        {
            sb.AppendLine(f.Type switch
            {
                FilterType.Peaking => string.Format(inv, "Filter: ON PK Fc {0:0.##} Hz Gain {1:0.##} dB Q {2:0.####}", f.Frequency, f.GainDb, f.Q),
                FilterType.LowShelf => string.Format(inv, "Filter: ON LSC Fc {0:0.##} Hz Gain {1:0.##} dB Q {2:0.####}", f.Frequency, f.GainDb, f.Q),
                FilterType.HighShelf => string.Format(inv, "Filter: ON HSC Fc {0:0.##} Hz Gain {1:0.##} dB Q {2:0.####}", f.Frequency, f.GainDb, f.Q),
                FilterType.LowPass => string.Format(inv, "Filter: ON LPQ Fc {0:0.##} Hz Q {1:0.####}", f.Frequency, f.Q),
                FilterType.HighPass => string.Format(inv, "Filter: ON HPQ Fc {0:0.##} Hz Q {1:0.####}", f.Frequency, f.Q),
                FilterType.Notch => string.Format(inv, "Filter: ON NO Fc {0:0.##} Hz Q {1:0.####}", f.Frequency, f.Q),
                _ => throw new ArgumentOutOfRangeException(nameof(f)),
            });
        }
        if (limiterLibrary is not null)
            sb.AppendLine($"VSTPlugin: Library \"{limiterLibrary}\""); // EAPO needs the quotes for paths with spaces
        return sb.ToString();
    }
}

/// <summary>RBJ Audio-EQ-Cookbook biquad (Q form).</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A0, double A1, double A2)
{
    public static Biquad For(EqFilter f, double fs)
    {
        double w0 = 2 * Math.PI * f.Frequency / fs, c = Math.Cos(w0), s = Math.Sin(w0);
        double a = Math.Pow(10, f.GainDb / 40), alpha = s / (2 * f.Q), sa = 2 * Math.Sqrt(a) * alpha;
        return f.Type switch
        {
            FilterType.Peaking => new(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a),
            FilterType.LowShelf => new(a * ((a + 1) - (a - 1) * c + sa), 2 * a * ((a - 1) - (a + 1) * c), a * ((a + 1) - (a - 1) * c - sa),
                (a + 1) + (a - 1) * c + sa, -2 * ((a - 1) + (a + 1) * c), (a + 1) + (a - 1) * c - sa),
            FilterType.HighShelf => new(a * ((a + 1) + (a - 1) * c + sa), -2 * a * ((a - 1) + (a + 1) * c), a * ((a + 1) + (a - 1) * c - sa),
                (a + 1) - (a - 1) * c + sa, 2 * ((a - 1) - (a + 1) * c), (a + 1) - (a - 1) * c - sa),
            FilterType.LowPass => new((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha),
            FilterType.HighPass => new((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha),
            FilterType.Notch => new(1, -2 * c, 1, 1 + alpha, -2 * c, 1 - alpha),
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };
    }

    public double MagnitudeDb(double f, double fs)
    {
        var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * f / fs); // z^-1
        var h = (B0 + B1 * z + B2 * z * z) / (A0 + A1 * z + A2 * z * z);
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-12));
    }
}
