namespace Hub.Core.Razer;

public sealed record CurvePoint(double X, double Y);

/// <summary>
/// Dynamic Sensitivity curve: a PCHIP monotone cubic through the control points (x = input speed in counts/ms,
/// y = output/input ratio), sampled into the 250 values the mouse stores. PCHIP is Fritsch &amp; Carlson (1980),
/// "Monotone Piecewise Cubic Interpolation", with the weighted-harmonic-mean slopes and three-point end slopes used by
/// MATLAB's pchip and SciPy's PchipInterpolator. Checked against curves read back from a Viper V3 Pro.
/// </summary>
public static class SensitivityCurve
{
    public const int SampleCount = 250;
    const double MinRatio = 0.1;

    /// <summary>Preview shapes for the firmware's built-in modes. Custom (3) uses whatever points the user drew.</summary>
    public static readonly IReadOnlyDictionary<DynamicSensitivityMode, CurvePoint[]> Templates = new Dictionary<DynamicSensitivityMode, CurvePoint[]>
    {
        [DynamicSensitivityMode.Classic] = [new(0, 1), new(20, 1.1), new(40, 1.2), new(70, 1.35), new(100, 1.5)],
        [DynamicSensitivityMode.Natural] = [new(0, 1), new(11, 1.45), new(20, 1.5), new(35, 1.5), new(100, 1.5)],
        [DynamicSensitivityMode.Jump] = [new(0, 1), new(9, 1), new(21, 1.5), new(24, 1.5), new(100, 1.5)],
    };

    public static Func<double, double> Spline(IReadOnlyList<CurvePoint> points)
    {
        var p = points.OrderBy(q => q.X).ToArray();
        int n = p.Length;
        if (n < 3) throw new ArgumentException("at least 3 points", nameof(points));
        var x = p.Select(q => q.X).ToArray();
        var y = p.Select(q => q.Y).ToArray();

        var m = new double[n - 1];
        for (int i = 0; i < n - 1; i++) m[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);

        var t = new double[n];
        double hPrev = x[1] - x[0];
        bool zeroPrev = Math.Abs(m[0]) < double.Epsilon;
        for (int i = 1; i < n - 1; i++)
        {
            double h = x[i + 1] - x[i];
            bool zero = Math.Abs(m[i]) < double.Epsilon;
            if (zero || zeroPrev || Math.Sign(m[i]) != Math.Sign(m[i - 1])) t[i] = 0;
            else
            {
                double w1 = 2 * h + hPrev, w2 = h + 2 * hPrev;
                t[i] = (w1 + w2) / (w1 / m[i - 1] + w2 / m[i]);
            }
            hPrev = h;
            zeroPrev = zero;
        }
        t[0] = EndSlope(x[1] - x[0], x[2] - x[1], m[0], m[1]);
        t[n - 1] = EndSlope(x[n - 1] - x[n - 2], x[n - 2] - x[n - 3], m[n - 2], m[n - 3]);

        var c = new (double C0, double C1, double C2, double C3)[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double h = x[i + 1] - x[i];
            c[i] = (y[i], t[i], (3 * (y[i + 1] - y[i]) / h - 2 * t[i] - t[i + 1]) / h, (2 * (y[i] - y[i + 1]) / h + t[i] + t[i + 1]) / (h * h));
        }

        return v =>
        {
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (x[mid] <= v) lo = mid; else hi = mid;
            }
            double u = v - x[lo];
            var k = c[lo];
            return k.C0 + u * (k.C1 + u * (k.C2 + u * k.C3));
        };
    }

    static double EndSlope(double h0, double h1, double m0, double m1)
    {
        double s = ((2 * h0 + h1) * m0 - h0 * m1) / (h0 + h1);
        if (Math.Sign(s) != Math.Sign(m0)) return 0;
        if (Math.Sign(m0) != Math.Sign(m1) && Math.Abs(s) > 3 * Math.Abs(m0)) return 3 * m0;
        return s;
    }

    /// <summary>The 250 values sent with 0B,13: sample i = curve at speed i+1, taken from a 1000-step sweep (the first sweep point that rounds to that speed).</summary>
    public static double[] Sample(IReadOnlyList<CurvePoint> points)
    {
        var f = Spline(points);
        double maxX = points.Max(q => q.X);
        var sweep = Enumerable.Range(0, 1000).Select(k => k * maxX / 999).Select(sx => (X: sx, Y: f(sx))).ToArray();

        var result = new double[SampleCount];
        double last = 0;
        for (int i = 0; i < SampleCount; i++)
        {
            int speed = i + 1;
            foreach (var (sx, sy) in sweep)
            {
                if (JsRound(sx) == speed) { last = sy; result[i] = Math.Max(MinRatio, last); break; }
                if (sx > speed) break;
            }
            if (result[i] == 0) result[i] = Math.Max(MinRatio, last);
        }
        return result;
    }

    static double JsRound(double v) => Math.Floor(v + 0.5);
}
