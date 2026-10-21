using Hub.Core.Diagnostics;

namespace Hub.Tests;

public class MouseTestTests
{
    const long F = 10_000_000; // ticks per second (QPC is 10 MHz on current Windows)

    static PollingMeter Meter(IEnumerable<long> timestamps)
    {
        var m = new PollingMeter(F);
        foreach (var t in timestamps) m.Add(t);
        return m;
    }

    [Fact]
    public void Steady_4000_Hz_reads_4000()
    {
        var s = Meter(Enumerable.Range(0, 8000).Select(i => i * 2500L)).Stats();
        Assert.InRange(s.Hz, 3960, 4040);
        Assert.True(s.Steady > 0.99);
    }

    [Fact]
    public void Jitter_lowers_steadiness()
    {
        long t = 0;
        var ts = new List<long>();
        for (int i = 0; i < 4000; i++) { ts.Add(t); t += i % 5 == 4 ? 5000 : 2500; }
        Assert.InRange(Meter(ts).Stats().Steady, 0.75, 0.85);
    }

    [Fact]
    public void Pauses_are_not_counted()
    {
        var burst1 = Enumerable.Range(0, 1000).Select(i => i * 10_000L);
        var burst2 = Enumerable.Range(0, 1000).Select(i => 3 * F + i * 10_000L);
        Assert.InRange(Meter(burst1.Concat(burst2)).Stats().Hz, 990, 1010);
    }

    [Fact]
    public void Dispatch_jitter_reads_the_real_rate_and_stays_steady()
    {
        // 4000 Hz with ±20 % timestamp jitter (messages are timestamped when dispatched, not when the report arrived)
        var rng = new Random(7);
        long t = 0;
        var ts = new List<long>();
        for (int i = 0; i < 8000; i++) { ts.Add(t); t += 2500 + rng.Next(-500, 501); }
        var s = Meter(ts).Stats();
        Assert.InRange(s.Hz, 3900, 4100);
        Assert.True(s.Steady > 0.9, $"steady {s.Steady:P0}");
    }

    [Fact]
    public void Moderate_movement_still_reads_the_polling_rate()
    {
        // a 2000 Hz mouse moved slowly skips polls with no motion: intervals of 0.5, 1, 1.5, 2 ms, a third at 0.5 ms
        long t = 0;
        var ts = new List<long>();
        long[] pattern = [5000, 10000, 5000, 15000, 5000, 20000];
        for (int i = 0; i < 3000; i++) { ts.Add(t); t += pattern[i % pattern.Length]; }
        Assert.InRange(Meter(ts).Stats().Hz, 1980, 2020);
    }

    [Fact]
    public void History_has_one_value_per_moving_second()
    {
        var s = Meter(Enumerable.Range(0, 3000).Select(i => i * 10_000L)).Stats(); // 3 s at 1000 Hz
        Assert.Equal(3, s.History.Count);
        Assert.All(s.History, hz => Assert.InRange(hz, 990, 1010));
    }

    static ClickChatter Clicks(double gapMs, double threshold = 30, int button = 0)
    {
        var c = new ClickChatter(F) { ThresholdMs = threshold };
        c.Down(button, 0); c.Up(button, F / 20);                       // 50 ms press
        long next = F / 20 + (long)(gapMs * F / 1000);
        c.Down(button, next); c.Up(button, next + F / 20);
        return c;
    }

    [Fact]
    public void A_press_8_ms_after_release_is_chatter() => Assert.Equal(1, Clicks(8).Stats().Single(b => b.Button == 0).Chatter);

    [Fact]
    public void A_press_120_ms_after_release_is_not() => Assert.Equal(0, Clicks(120).Stats().Single(b => b.Button == 0).Chatter);

    [Fact]
    public void Threshold_is_adjustable()
    {
        Assert.Equal(0, Clicks(40, threshold: 30).Stats().Single().Chatter);
        Assert.Equal(1, Clicks(40, threshold: 50).Stats().Single().Chatter);
    }

    [Fact]
    public void Moving_the_threshold_rejudges_earlier_presses()
    {
        var c = Clicks(40, threshold: 30);
        Assert.Equal(0, c.Stats().Single().Chatter);
        c.ThresholdMs = 50;
        Assert.Equal(1, c.Stats().Single().Chatter);
    }

    [Fact]
    public void Buttons_are_counted_separately()
    {
        var c = new ClickChatter(F);
        c.Down(0, 0); c.Up(0, 100); c.Down(1, 200); c.Up(1, 300); c.Down(1, 300 + F); c.Up(1, 400 + F);
        var stats = c.Stats();
        Assert.Equal(1, stats.Single(b => b.Button == 0).Presses);
        Assert.Equal(2, stats.Single(b => b.Button == 1).Presses);
        Assert.Equal(1000, stats.Single(b => b.Button == 1).FastestGapMs!.Value, 0);
    }
}
