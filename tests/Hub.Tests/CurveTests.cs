using Hub.Core.Razer;

namespace Hub.Tests;

public class CurveTests
{
    [Theory]
    [InlineData(0, 1.0240546639077568)]
    [InlineData(1, 1.0709905863484817)]
    [InlineData(4, 1.1996995315662813)]
    [InlineData(9, 1.3558078392075783)]
    [InlineData(14, 1.4566638757877723)]
    [InlineData(19, 1.5)]
    [InlineData(249, 1.5)]
    public void Matches_samples_read_back_from_a_viper_v3_pro(int index, double stored)
    {
        // A custom curve drawn with these control points, and the samples the mouse returned for it (0B,93).
        CurvePoint[] points = [new(0, 1), new(9.668316831683168, 1.3592308044433594), new(19.128712871287128, 1.5), new(24, 1.5), new(100, 1.5)];

        var samples = SensitivityCurve.Sample(points);

        Assert.Equal(250, samples.Length);
        Assert.Equal(stored, samples[index], 12);
    }

    [Fact]
    public void Templates_are_monotone_and_start_at_one()
    {
        foreach (var (_, points) in SensitivityCurve.Templates)
        {
            var s = SensitivityCurve.Sample(points);
            Assert.InRange(s[0], 1.0, 1.1);
            for (int i = 1; i < s.Length; i++) Assert.True(s[i] >= s[i - 1] - 1e-12);
        }
    }
}
