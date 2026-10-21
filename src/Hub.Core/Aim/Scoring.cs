namespace Hub.Core.Aim;

public static class Scoring
{
    /// <summary>Hits per second, weighted by accuracy squared: speed counts, accuracy counts more.</summary>
    public static double RoundScore(int hits, int clicks, double seconds) =>
        clicks == 0 || seconds <= 0 ? 0 : hits / seconds * Math.Pow(hits / (double)clicks, 2);

    /// <summary>How often the crosshair crossed the target on the way to it: sign changes of the signed yaw error.</summary>
    public static int Overshoots(IReadOnlyList<double> signedErrors)
    {
        int count = 0;
        for (int i = 1; i < signedErrors.Count; i++)
            if (Math.Sign(signedErrors[i]) != 0 && Math.Sign(signedErrors[i - 1]) != 0 && Math.Sign(signedErrors[i]) != Math.Sign(signedErrors[i - 1]))
                count++;
        return count;
    }
}
