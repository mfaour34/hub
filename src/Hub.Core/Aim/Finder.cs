namespace Hub.Core.Aim;

/// <summary>
/// Finds the player's sensitivity by comparison, in the spirit of the "perfect sensitivity approximation": each step
/// plays a lower and a higher setting around the current centre, the centre moves halfway (geometrically) toward the
/// better one, and the spread shrinks. Five steps cover ±50 % down to about ±1 %. Works in cm/360, so the result
/// applies to every game.
/// </summary>
public sealed class Finder(double startCm360)
{
    public const int Steps = 5;
    const double StartFactor = 1.5;

    double _centre = startCm360, _factor = StartFactor;

    public int Step { get; private set; }

    /// <summary>The two settings to compare next (lower cm/360 = faster), or null when done.</summary>
    public (double LowCm, double HighCm)? NextPair() => Step >= Steps ? null : (_centre / _factor, _centre * _factor);

    /// <param name="lowScore">Score of the round at LowCm.</param>
    /// <param name="highScore">Score of the round at HighCm.</param>
    public void Record(double lowScore, double highScore)
    {
        if (Step >= Steps) return;
        double half = Math.Sqrt(_factor);
        if (lowScore > highScore) _centre /= half;
        else if (highScore > lowScore) _centre *= half;
        _factor = Math.Sqrt(_factor);
        Step++;
    }

    public double ResultCm360 => _centre;
}
