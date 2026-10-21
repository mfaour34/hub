using System.Text.RegularExpressions;

namespace Hub.Core.Ui;

/// <summary>
/// The UI's theme vocabulary: the token names a theme sets (wwwroot/themes.js turns them into CSS variables) and
/// the presets it can name. User-edited themes come back from the page and settings.json, so they are checked here.
/// </summary>
public static partial class ThemeTokens
{
    public static readonly IReadOnlyList<string> Keys =
    [
        "bg", "side", "panel", "panel2", "line", "text", "muted", "accent", "accentText", "inv", "invText",
        "good", "warn", "bad", "game", "chat", "media", "labelFont",
    ];

    public static readonly IReadOnlyList<string> Presets =
        ["instrument-light", "instrument-dark", "fluent", "monochrome", "studio", "custom"];

    public const string DefaultPreset = "instrument-light";

    public static bool IsPreset(string? name) => name is not null && Presets.Contains(name);

    /// <summary>Known keys with valid values only: colours as #rrggbb (lower case), labelFont as mono or sans.</summary>
    public static Dictionary<string, string> Sanitize(IDictionary<string, string>? input)
    {
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in input ?? new Dictionary<string, string>())
        {
            if (!Keys.Contains(key) || value is null) continue;
            if (key == "labelFont") { if (value is "mono" or "sans") result[key] = value; }
            else if (HexColor().IsMatch(value)) result[key] = value.ToLowerInvariant();
        }
        return result;
    }

    /// <summary>
    /// The window colour before the page paints: with Follow Windows on, Instrument's light or dark background by
    /// Windows' current mode (it may have changed while Hub was closed); otherwise the background the page saved.
    /// </summary>
    public static string BackgroundFor(string? preset, bool followWindows, bool windowsDark, string? saved) =>
        followWindows && preset is "instrument-light" or "instrument-dark" ? (windowsDark ? "#161513" : "#dedbd3")
        : IsColor(saved) ? saved!.ToLowerInvariant() : "#dedbd3";

    public static bool IsColor(string? value) => value is not null && HexColor().IsMatch(value);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
