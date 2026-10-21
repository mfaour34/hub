using Hub.Core.Aim;

namespace Hub.Tests;

public class AimTests
{
    [Fact]
    public void Cs2_0_9_at_800_dpi_is_57_7_cm() =>
        Assert.Equal(57.73, Sensitivity.Cm360(Games.Get("cs2"), 0.9, 800), 2);

    [Fact]
    public void Cs2_0_9_is_Valorant_0_283() =>
        Assert.Equal(0.2829, Sensitivity.Convert("cs2", 0.9, "valorant")!.Value, 4);

    [Fact]
    public void Round_trip_is_exact()
    {
        foreach (var a in Games.All)
            foreach (var b in Games.All)
            {
                var there = Sensitivity.Convert(a.Id, a.DefaultSens, b.Id)!.Value;
                Assert.Equal(a.DefaultSens, Sensitivity.Convert(b.Id, there, a.Id)!.Value, 9);
            }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Bad_input_converts_to_null(double sens) => Assert.Null(Sensitivity.Convert("cs2", sens, "valorant"));

    [Fact]
    public void Pubg_curve_matches_its_published_formula()
    {
        // slider 50 → stored 0.02 → 0.02 × 2.2222 × 90/80 deg/count at FOV 90
        Assert.Equal(0.02 * 2.2222 * 90 / 80, Games.Get("pubg").DegPerCount(50), 6);
        Assert.Equal(50, Games.Get("pubg").SensFor(Games.Get("pubg").DegPerCount(50)), 9);
    }

    [Fact]
    public void Fortnite_takes_the_displayed_percentage() =>
        Assert.Equal(0.9 * 0.022 / 0.005555, Sensitivity.Convert("cs2", 0.9, "fortnite")!.Value, 6);

    [Fact]
    public void Hfov_16_9_to_16_10() => Assert.Equal(100.4, Sensitivity.HFovAt(106.26, 16.0 / 10), 1);

    [Fact]
    public void Every_game_has_a_positive_turn_rate_and_a_name() =>
        Assert.All(Games.All, g => { Assert.True(g.DegPerCount(g.DefaultSens) > 0); Assert.False(string.IsNullOrWhiteSpace(g.Name)); });

    [Fact]
    public void Cs2_fov_is_90_on_4_3_which_is_106_26_at_16_9() => Assert.Equal(106.26, Games.Get("cs2").HFov169, 2);

    [Fact]
    public void Cs2_and_valorant_come_first() => Assert.Equal(["cs2", "valorant"], Games.All.Take(2).Select(g => g.Id));
}

public class FinderTests
{
    static double Run(double start, Func<int, (double low, double high)> scores)
    {
        var f = new Finder(start);
        while (f.NextPair() is { } pair) { var (lo, hi) = scores(f.Step); f.Record(lo, hi); }
        return f.ResultCm360;
    }

    [Fact]
    public void Finder_moves_toward_the_better_side() => Assert.True(Run(57.7, _ => (1, 2)) > 57.7 * 1.4);

    [Fact]
    public void Finder_converges_when_the_centre_wins() =>
        Assert.InRange(Run(57.7, step => step % 2 == 0 ? (2, 1) : (1, 2)), 57.7 * 0.85, 57.7 * 1.15);

    [Fact]
    public void Finder_stops_after_five_steps()
    {
        var f = new Finder(40);
        int pairs = 0;
        while (f.NextPair() is not null) { pairs++; f.Record(1, 1); }
        Assert.Equal(Finder.Steps, pairs);
        Assert.Equal(40, f.ResultCm360, 9); // ties keep the centre
    }

    [Fact]
    public void Score_rewards_accuracy_more_than_speed() =>
        Assert.True(Scoring.RoundScore(10, 10, 10) > Scoring.RoundScore(12, 20, 10));

    [Fact]
    public void No_clicks_scores_zero() => Assert.Equal(0, Scoring.RoundScore(0, 0, 5));

    [Fact]
    public void Overshoots_count_sign_changes() => Assert.Equal(2, Scoring.Overshoots([5, 2, -1, 0.5, 0.1]));
}
