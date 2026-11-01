using System.Globalization;
using System.Text.RegularExpressions;

namespace Hub.Core.Audio;

/// <summary>
/// Reads an Equalizer APO config (a preset .txt like AutoEq's ParametricEQ.txt) into Hub's filters. Only what Hub
/// reproduces exactly is imported: Preamp lines and RBJ filters given with a Q. Everything else is listed in
/// <see cref="Result.Skipped"/> instead of being dropped silently, and so is everything after a line that limits the
/// rest of the file to some channels, devices or conditions (Hub plays one EQ on both channels).
/// </summary>
public static partial class EqualizerApoImport
{
    /// <summary>Equalizer APO's LP/HP without a Q are 12 dB/octave Butterworth filters.</summary>
    public const double ButterworthQ = 0.7071067811865476;

    public sealed record SkippedLine(int Line, string Text);

    public sealed record Result(double PreampDb, List<EqFilter> Filters, List<SkippedLine> Skipped);

    public static Result Parse(string text)
    {
        double preamp = 0;
        var filters = new List<EqFilter>();
        var skipped = new List<SkippedLine>();
        var lines = text.Replace("\r", "").Split('\n');
        bool scoped = false; // after a Channel:/Device:/If:/Stage: line the rest only applies to part of the audio
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!scoped && ScopeLine().Match(line) is { Success: true } sc)
            {
                if (sc.Groups[1].Value.Equals("Channel", StringComparison.OrdinalIgnoreCase) && sc.Groups[2].Value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                    continue; // all channels: what Hub does anyway
                scoped = true;
            }
            if (scoped)
                skipped.Add(new SkippedLine(i + 1, line));
            else if (PreampLine().Match(line) is { Success: true } p)
                preamp += Num(p.Groups[1].Value); // Equalizer APO adds them up
            else if (FilterLine().Match(line) is { Success: true } f && ToFilter(f) is { } filter)
                filters.Add(filter);
            else
                skipped.Add(new SkippedLine(i + 1, line));
        }
        return new Result(preamp, filters, skipped);
    }

    static EqFilter? ToFilter(Match m)
    {
        bool on = m.Groups["state"].Value.Equals("ON", StringComparison.OrdinalIgnoreCase);
        var rest = m.Groups["rest"].Value;
        var fc = FcPart().Match(rest);
        if (!fc.Success) return null;
        double freq = Num(fc.Groups[1].Value) * (fc.Groups[2].Value.Equals("kHz", StringComparison.OrdinalIgnoreCase) ? 1000 : 1);
        var gainMatch = GainPart().Match(rest);
        double gain = gainMatch.Success ? Num(gainMatch.Groups[1].Value) : 0;
        var qMatch = QPart().Match(rest);
        double? q = qMatch.Success ? Num(qMatch.Groups[1].Value) : null;
        if (BandwidthPart().IsMatch(rest)) return null; // BW Oct: a different bandwidth definition

        (FilterType type, bool needsGain, double? defaultQ)? kind = m.Groups["type"].Value.ToUpperInvariant() switch
        {
            "PK" or "PEQ" => (FilterType.Peaking, true, null),
            "LSC" => (FilterType.LowShelf, true, null),
            "HSC" => (FilterType.HighShelf, true, null),
            "LP" => (FilterType.LowPass, false, ButterworthQ),
            "HP" => (FilterType.HighPass, false, ButterworthQ),
            "LPQ" => (FilterType.LowPass, false, null),
            "HPQ" => (FilterType.HighPass, false, null),
            "NO" => (FilterType.Notch, false, null),
            _ => null, // LS/HS (fixed slope), BP, AP and others aren't Hub filters
        };
        if (kind is not var (t, needsGain, defQ)) return null;
        if (needsGain && !gainMatch.Success) return null;
        if ((q ?? defQ) is not { } qValue || qValue <= 0 || freq <= 0) return null;
        return new EqFilter(t, freq, needsGain ? gain : 0, qValue, on);
    }

    /// <summary>"IEM_Target.txt" → "IEM Target"; AutoEq's " ParametricEQ" suffix is dropped.</summary>
    public static string NameFromFile(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName.Replace('\\', '/').Split('/')[^1]);
        name = name.Replace('_', ' ').Trim();
        if (name.EndsWith(" ParametricEQ", StringComparison.OrdinalIgnoreCase)) name = name[..^" ParametricEQ".Length].Trim();
        return name.Length == 0 ? "Imported" : name;
    }

    static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(Channel|Device|If|ElseIf|Else|EndIf|Stage)\s*:(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ScopeLine();

    [GeneratedRegex(@"^Preamp:\s*([-+]?\d+(?:\.\d+)?)\s*dB$", RegexOptions.IgnoreCase)]
    private static partial Regex PreampLine();

    [GeneratedRegex(@"^Filter(?:\s*\d+)?\s*:\s*(?<state>ON|OFF)\s+(?<type>[A-Z]+)\b(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FilterLine();

    [GeneratedRegex(@"\bFc\s+(\d+(?:\.\d+)?)\s*(Hz|kHz)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FcPart();

    [GeneratedRegex(@"\bGain\s+([-+]?\d+(?:\.\d+)?)\s*dB\b", RegexOptions.IgnoreCase)]
    private static partial Regex GainPart();

    [GeneratedRegex(@"\bQ\s+(\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QPart();

    [GeneratedRegex(@"\bBW\b", RegexOptions.IgnoreCase)]
    private static partial Regex BandwidthPart();
}
