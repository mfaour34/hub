namespace Hub.Core.Aim;

public static class Sensitivity
{
    /// <summary>Centimetres of mouse travel for one full 360° turn.</summary>
    public static double Cm360(Game game, double sens, double dpi) => 360 / (game.DegPerCount(sens) * dpi) * 2.54;

    /// <summary>The setting in <paramref name="game"/> that gives <paramref name="cm360"/> at <paramref name="dpi"/>.</summary>
    public static double SensFor(Game game, double cm360, double dpi) => game.SensFor(360 * 2.54 / (cm360 * dpi));

    /// <summary>The same turn per count in another game (DPI cancels out); null for a setting that turns nothing.</summary>
    public static double? Convert(string fromGame, double sens, string toGame)
    {
        if (!double.IsFinite(sens) || sens <= 0) return null;
        double deg = Games.Get(fromGame).DegPerCount(sens);
        if (!double.IsFinite(deg) || deg <= 0) return null;
        var result = Games.Get(toGame).SensFor(deg);
        return double.IsFinite(result) ? result : null;
    }

    /// <summary>Hor+ scaling: a game's horizontal FOV at 16:9 as the horizontal FOV at another aspect ratio.</summary>
    public static double HFovAt(double hFov169, double aspect) => VerticalToHorizontal(HorizontalToVertical(hFov169, 16.0 / 9), aspect);

    public static double VerticalToHorizontal(double vFov, double aspect) => Deg(2 * Math.Atan(Math.Tan(Rad(vFov) / 2) * aspect));

    public static double HorizontalToVertical(double hFov, double aspect) => Deg(2 * Math.Atan(Math.Tan(Rad(hFov) / 2) / aspect));

    static double Rad(double d) => d * Math.PI / 180;
    static double Deg(double r) => r * 180 / Math.PI;
}
