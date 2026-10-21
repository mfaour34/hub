using Hub.Core.Ui;

namespace Hub.Tests;

public class ThemeTests
{
    [Fact]
    public void Sanitize_keeps_valid_colours_lowercased() =>
        Assert.Equal("#ff5a1f", ThemeTokens.Sanitize(new Dictionary<string, string> { ["accent"] = "#FF5A1F" })["accent"]);

    [Fact]
    public void Sanitize_drops_unknown_keys_and_bad_values() =>
        Assert.Empty(ThemeTokens.Sanitize(new Dictionary<string, string> { ["accent"] = "red", ["bogus"] = "#000000", ["bg"] = "#12345" }));

    [Fact]
    public void Sanitize_accepts_label_font_mono_or_sans_only()
    {
        Assert.Equal("mono", ThemeTokens.Sanitize(new Dictionary<string, string> { ["labelFont"] = "mono" })["labelFont"]);
        Assert.Empty(ThemeTokens.Sanitize(new Dictionary<string, string> { ["labelFont"] = "comic" }));
    }

    [Fact]
    public void Sanitize_null_is_empty() => Assert.Empty(ThemeTokens.Sanitize(null));

    [Fact]
    public void IsPreset_knows_the_six_presets()
    {
        Assert.All(ThemeTokens.Presets, p => Assert.True(ThemeTokens.IsPreset(p)));
        Assert.Equal(6, ThemeTokens.Presets.Count);
        Assert.False(ThemeTokens.IsPreset("neon"));
        Assert.False(ThemeTokens.IsPreset(null));
    }
}

public class WindowBackgroundTests
{
    [Theory]
    [InlineData("instrument-light", true, true, "#dedbd3", "#161513")]   // follow on, Windows dark: dark even though light was saved
    [InlineData("instrument-dark", true, false, "#161513", "#dedbd3")]   // follow on, Windows light
    [InlineData("fluent", true, true, "#1b1d22", "#1b1d22")]             // follow only applies to Instrument
    [InlineData("instrument-light", false, true, "#dedbd3", "#dedbd3")]  // follow off: what the page saved
    [InlineData("custom", false, false, "not-a-colour", "#dedbd3")]      // bad saved value: Instrument light
    public void BackgroundFor_resolves_follow_and_falls_back(string preset, bool follow, bool windowsDark, string saved, string expected) =>
        Assert.Equal(expected, ThemeTokens.BackgroundFor(preset, follow, windowsDark, saved));
}
