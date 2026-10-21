namespace Hub.Core.Diagnostics;

/// <param name="Hz">
/// The polling rate over the last 2 s of movement: 1 / the median interval when moving fast (timestamps carry
/// dispatch jitter, which the median ignores), else 1 / the 10th-percentile interval (slow movement skips polls
/// with no motion in them, stretching most intervals; the quickest ones are still the polling period).
/// </param>
/// <param name="Steady">Share of intervals within ±25 % of the period: how evenly reports arrive (0–1).</param>
/// <param name="FastEnough">Movement fast enough that nearly every poll carries motion, so Steady means something.</param>
/// <param name="History">Reports per second for each of the last 10 seconds of movement, oldest first.</param>
public sealed record PollingStats(double Hz, double Steady, int Samples, IReadOnlyList<double> History, bool FastEnough = false)
{
    public static readonly PollingStats Empty = new(0, 0, 0, []);
}

/// <summary>
/// The mouse's real report rate from raw-input timestamps. Pauses (no report for 50 ms) are left out, so only
/// time spent moving counts. Thread-safe: the raw-input thread adds while the page reads.
/// </summary>
public sealed class PollingMeter(long ticksPerSecond)
{
    const int Capacity = 1 << 17; // 32 s at 4000 Hz, 16 s at 8000 Hz
    readonly long[] _ring = new long[Capacity];
    readonly object _gate = new();
    long _count;

    public void Add(long timestamp)
    {
        lock (_gate) _ring[_count++ % Capacity] = timestamp;
    }

    public PollingStats Stats()
    {
        long[] ts;
        lock (_gate)
        {
            int n = (int)Math.Min(_count, Capacity);
            ts = new long[n];
            for (int i = 0; i < n; i++) ts[i] = _ring[(_count - n + i) % Capacity];
        }
        long gap = ticksPerSecond / 20; // 50 ms
        var intervals = new List<long>(ts.Length);
        for (int i = 1; i < ts.Length; i++)
        {
            long d = ts[i] - ts[i - 1];
            if (d > 0 && d <= gap) intervals.Add(d);
        }
        if (intervals.Count == 0) return PollingStats.Empty;

        // rate and steadiness over the last 2 s of movement
        var recent = new List<long>();
        long moving = 0;
        for (int i = intervals.Count - 1; i >= 0 && moving < 2 * ticksPerSecond; i--) { recent.Add(intervals[i]); moving += intervals[i]; }
        var sorted = recent.OrderBy(d => d).ToList();
        double quick = sorted[sorted.Count / 10], median = sorted[sorted.Count / 2];
        bool fast = median <= quick * 1.5;
        double period = fast ? median : quick;
        double steady = recent.Count(d => Math.Abs(d - period) <= period * 0.25) / (double)recent.Count;

        // one value per second of movement; a last part-second counts once it is at least half a second
        var history = new List<double>();
        long bucketTime = 0; int bucketCount = 0;
        foreach (var d in intervals)
        {
            bucketTime += d; bucketCount++;
            if (bucketTime >= ticksPerSecond) { history.Add(bucketCount * (double)ticksPerSecond / bucketTime); bucketTime = 0; bucketCount = 0; }
        }
        if (bucketTime >= ticksPerSecond / 2) history.Add(bucketCount * (double)ticksPerSecond / bucketTime);

        return new PollingStats(ticksPerSecond / period, steady, recent.Count, history.TakeLast(10).ToList(), fast);
    }
}

/// <param name="Button">0 left, 1 right, 2 middle, 3 back, 4 forward.</param>
/// <param name="Chatter">Presses that came sooner after the previous release than a finger can (a bouncing switch).</param>
/// <param name="FastestGapMs">Shortest release-to-press gap seen.</param>
public sealed record ButtonStats(int Button, int Presses, int Chatter, double? FastestGapMs);

/// <summary>
/// Counts presses per button and flags those that follow a release within <see cref="ThresholdMs"/>. The gaps are
/// kept, so moving the threshold re-judges presses already made.
/// </summary>
public sealed class ClickChatter(long ticksPerSecond)
{
    sealed class State { public int Presses; public long? LastUp; public readonly List<double> GapsMs = []; }

    readonly SortedDictionary<int, State> _buttons = [];
    readonly object _gate = new();

    public double ThresholdMs { get; set; } = 30;

    public void Down(int button, long timestamp)
    {
        lock (_gate)
        {
            var s = Get(button);
            s.Presses++;
            if (s.LastUp is { } up) s.GapsMs.Add((timestamp - up) * 1000.0 / ticksPerSecond);
        }
    }

    public void Up(int button, long timestamp)
    {
        lock (_gate) Get(button).LastUp = timestamp;
    }

    public IReadOnlyList<ButtonStats> Stats()
    {
        lock (_gate)
            return _buttons.Select(b => new ButtonStats(b.Key, b.Value.Presses, b.Value.GapsMs.Count(g => g < ThresholdMs),
                b.Value.GapsMs.Count > 0 ? b.Value.GapsMs.Min() : null)).ToList();
    }

    public void Reset()
    {
        lock (_gate) _buttons.Clear();
    }

    State Get(int button) => _buttons.TryGetValue(button, out var s) ? s : _buttons[button] = new State();
}
