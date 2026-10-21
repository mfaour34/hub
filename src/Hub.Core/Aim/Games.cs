namespace Hub.Core.Aim;

/// <param name="DegPerCount">Degrees the view turns per mouse count at a given in-game sensitivity.</param>
/// <param name="SensFor">Inverse of <paramref name="DegPerCount"/>: the setting that turns that many degrees per count.</param>
/// <param name="HFov169">The game's default horizontal field of view at 16:9, for the aim lab's camera.</param>
/// <param name="Verified">False where published sources disagree; the UI marks those values as approximate.</param>
public sealed record Game(string Id, string Name, Func<double, double> DegPerCount, Func<double, double> SensFor,
    double DefaultSens, double HFov169, string Notes, bool Verified = true);

/// <summary>
/// Hip-fire turn rates. Sources: csdb.gg's converter, DPI Wizard's measurements (via the
/// Source2AllSensitivityConverter catalog), Ubisoft's note on Siege's MouseSensitivityMultiplierUnit, senslab.pro.
/// Rust and Battlefield are left out: their published values disagree too much.
/// </summary>
public static class Games
{
    static Game Linear(string id, string name, double yaw, double defaultSens, double hfov, string notes = "", bool verified = true) =>
        new(id, name, s => yaw * s, d => d / yaw, defaultSens, hfov, notes, verified);

    /// <summary>A FOV measured on a 4:3 frame (Source engine style), as horizontal degrees at 16:9.</summary>
    static double From43(double fov43) => Sensitivity.VerticalToHorizontal(Sensitivity.HorizontalToVertical(fov43, 4.0 / 3), 16.0 / 9);

    /// <summary>A vertical FOV as horizontal degrees at 16:9.</summary>
    static double FromVertical(double vfov) => Sensitivity.VerticalToHorizontal(vfov, 16.0 / 9);

    // PUBG (first person, FOV 90): stored = 0.02 × 10^((S − 50) / 50); deg/count = stored × 2.2222 × FOV / 80
    const double PubgScale = 0.02 * 2.2222 * 90 / 80;

    public static readonly IReadOnlyList<Game> All =
    [
        Linear("cs2", "Counter-Strike 2", 0.022, 1.0, From43(90)),
        Linear("valorant", "Valorant", 0.07, 0.35, 103),
        Linear("apex", "Apex Legends", 0.022, 2.0, From43(90), "hip fire; scopes have their own multipliers"),
        Linear("overwatch", "Overwatch 2", 0.0066, 5.0, 103),
        Linear("cod", "Call of Duty (MW 2019 and later, Warzone)", 0.0066, 5.0, From43(80), "hip fire; ADS depends on Legacy/Relative"),
        Linear("fortnite", "Fortnite", 0.005555, 10, 80, "the percentage shown in the game's menu"),
        Linear("r6", "Rainbow Six Siege", 0.00572958, 10, FromVertical(60), "with the default MouseSensitivityMultiplierUnit 0.02"),
        Linear("marvel", "Marvel Rivals", 0.0175, 5.0, 100, "third-person camera"),
        Linear("tarkov", "Escape from Tarkov", 0.125, 0.3, FromVertical(75)),
        Linear("deadlock", "Deadlock", 0.044, 1.0, 100),
        Linear("finals", "The Finals", 0.001, 30, 100, "sources disagree; check with an in-game 360", verified: false),
        new("pubg", "PUBG (first person, FOV 90)", s => PubgScale * Math.Pow(10, (s - 50) / 50), d => 50 + 50 * Math.Log10(d / PubgScale),
            50, 90, "the 0–100 slider; turn rate also follows the FOV setting"),
        Linear("tf2", "Team Fortress 2", 0.022, 3.0, From43(90)),
        Linear("quake", "Quake Champions", 0.022, 3.0, 103),
    ];

    public static Game Get(string id) => All.FirstOrDefault(g => g.Id == id) ?? throw new ArgumentException($"Unknown game “{id}”");
}
