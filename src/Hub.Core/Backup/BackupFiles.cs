using System.Globalization;
using System.Text;
using Hub.Core.Audio;

namespace Hub.Core.Backup;

/// <summary>What one device plays on one channel, as Equalizer APO text (built-in presets included).</summary>
public sealed record InUseProfile(string Device, string Channel, string ApoText);

/// <summary>
/// The files of a backup, the same in the GitHub repo and in an export: audio.json and mouse.json hold everything
/// (a restore reads only those two), the .txt files are Equalizer APO presets for use in other software.
/// </summary>
public static class BackupFiles
{
    public const string AudioFile = "audio.json", MouseFile = "mouse.json";
    /// <summary>Larger files aren't a Hub backup; refused before parsing.</summary>
    public const int MaxFileBytes = 1 << 20;

    public static readonly string[] Channels = ["Game", "Chat", "Media", "Music"];
    public static readonly string[] ChannelIds = ["game", "chat", "media", "music"];
    public const double MaxTameDb = 12;

    /// <param name="apoText">Equalizer APO text for one of the user's profiles, with the preamp Hub plays it at.</param>
    public static SortedDictionary<string, string> Build(AudioBackup audio, MouseBackup? mouse, IEnumerable<InUseProfile> inUse,
        Func<ProfileBackup, string> apoText)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [AudioFile] = BackupJson.Write(audio),
            ["README.md"] = Readme,
        };
        if (mouse is not null) files[MouseFile] = BackupJson.Write(mouse);
        foreach (var (channel, profiles) in audio.Profiles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            foreach (var p in profiles)
                Add(files, $"profiles/{channel.ToLowerInvariant()}/", p.Name, Lf(apoText(p)));
        foreach (var u in inUse)
            Add(files, "in use/", $"{u.Device} - {u.Channel.ToLowerInvariant()}", Lf(u.ApoText));
        return files;
    }

    /// <summary>Adds "folder/name.txt", with a number when two names only differ in characters a file name can't hold.</summary>
    static void Add(SortedDictionary<string, string> files, string folder, string name, string text)
    {
        var baseName = SafeFileName(name);
        var path = $"{folder}{baseName}.txt";
        for (int n = 2; files.Keys.Any(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)); n++) path = $"{folder}{baseName} ({n}).txt";
        files[path] = text;
    }

    /// <summary>A file name that works on Windows, macOS and Linux and in a GitHub path.</summary>
    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.').Trim();
        if (s.Length > 80) s = s[..80].Trim();
        if (s.Length == 0 || s.All(c => c == '.')) s = "profile";
        // names Windows reserves for devices
        var stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            s = "_" + s;
        return s;
    }

    static string Lf(string text) => text.Replace("\r\n", "\n");

    // ---------------- reading a backup back ----------------

    /// <summary>Reads and checks audio.json (and mouse.json when present). Throws with the first problem found.</summary>
    public static (AudioBackup Audio, MouseBackup? Mouse) Parse(string audioJson, string? mouseJson)
    {
        CheckSize(audioJson, AudioFile);
        AudioBackup audio;
        try { audio = BackupJson.Read<AudioBackup>(audioJson); }
        catch (System.Text.Json.JsonException e) { throw new InvalidDataException($"{AudioFile} isn't valid: {e.Message}"); }
        Validate(audio);

        MouseBackup? mouse = null;
        if (mouseJson is not null)
        {
            CheckSize(mouseJson, MouseFile);
            try { mouse = BackupJson.Read<MouseBackup>(mouseJson); }
            catch (System.Text.Json.JsonException e) { throw new InvalidDataException($"{MouseFile} isn't valid: {e.Message}"); }
            Validate(mouse);
        }
        return (audio, mouse);
    }

    static void CheckSize(string text, string file)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) throw new InvalidDataException($"{file} is too large to be a Hub backup");
    }

    static void Check(bool ok, string problem)
    {
        if (!ok) throw new InvalidDataException(problem);
    }

    public static void Validate(AudioBackup a)
    {
        Check(a.Version == 1, $"{AudioFile} is from a newer Hub (version {a.Version})");
        Check(a.Profiles is not null && a.Devices is not null && a.AppChannels is not null && a.Volumes is not null && a.HeadsetEq is not null,
            $"{AudioFile} is missing a section");
        foreach (var (channel, profiles) in a.Profiles!)
        {
            Check(Channels.Contains(channel), $"Unknown channel “{channel}”");
            Check(profiles is not null && profiles.Count <= 200, $"Too many {channel} profiles");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in profiles!)
            {
                Check(p is not null && p.Filters is not null, $"A {channel} profile is incomplete");
                CheckName(p!.Name, $"A {channel} profile");
                Check(names.Add(p.Name), $"Two {channel} profiles are called “{p.Name}”");
                CheckProfile(p);
            }
        }
        Check(a.Devices!.Count <= 100, "Too many devices");
        foreach (var d in a.Devices)
        {
            Check(d is not null && d.Presets is not null && d.Tame is not null, "A device entry is incomplete");
            CheckName(d!.Name, "A device", max: 200);
            foreach (var (channel, profile) in d.Presets!)
            {
                Check(Channels.Contains(channel), $"Unknown channel “{channel}” on {d.Name}");
                CheckName(profile, $"{d.Name}'s {channel} profile");
            }
            foreach (var (channel, db) in d.Tame!)
                Check(Channels.Contains(channel) && Finite(db) && db is >= 0 and <= MaxTameDb, $"{d.Name}'s {channel} tame level is out of range");
        }
        Check(a.Devices.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == a.Devices.Count, "A device is listed twice");
        Check(a.DefaultDevice is null || a.Devices.Any(d => d.Name == a.DefaultDevice), "The default device isn't in the backup");
        Check(a.AppChannels!.Count <= 1000, "Too many apps");
        foreach (var (app, channel) in a.AppChannels)
        {
            CheckName(app, "An app", max: 260);
            Check(ChannelIds.Contains(channel), $"{app} is on an unknown channel “{channel}”");
        }
        foreach (var (channel, v) in a.Volumes!)
            Check(Channels.Contains(channel) && Finite(v) && v is >= 0 and <= 1, $"The {channel} volume is out of range");
        Check(Finite(a.ChatMix) && a.ChatMix is >= -1 and <= 1, "ChatMix is out of range");
        Check(a.HeadsetEq!.Count <= 100, "Too many headset EQ presets");
        foreach (var h in a.HeadsetEq)
        {
            Check(h is not null && h.Gains is not null, "A headset EQ preset is incomplete");
            CheckName(h!.Name, "A headset EQ preset");
            Check(h.Gains!.Length == 5 && h.Gains.All(g => g is >= -12 and <= 12), $"Headset EQ “{h.Name}” needs 5 gains of -12..12 dB");
        }
    }

    /// <summary>The same limits as saving a profile in Hub.</summary>
    public static void CheckProfile(ProfileBackup p)
    {
        Check(p.Filters.All(f => f is not null && Enum.IsDefined(f.Type)), $"“{p.Name}” has an empty or unknown filter");
        Check(p.Filters.Count <= 100 && p.Filters.Count(f => f.Enabled) <= 20, $"“{p.Name}” has more than 20 filters");
        foreach (var f in p.Filters)
        {
            Check(Finite(f.Frequency) && f.Frequency is >= 10 and < 24000, $"“{p.Name}”: filter frequency {f.Frequency} Hz is out of range");
            Check(Finite(f.Q) && f.Q is >= 0.05 and <= 50, $"“{p.Name}”: Q {f.Q} is out of range (0.05–50)");
            Check(Finite(f.GainDb) && Math.Abs(f.GainDb) <= 24, $"“{p.Name}”: filter gain {f.GainDb} dB is out of range (±24)");
        }
        Check(Finite(p.GainDb) && Math.Abs(p.GainDb) <= 24, $"“{p.Name}”: gain is out of range (±24 dB)");
        Check(p.Source is null || p.Source.Length <= 260, $"“{p.Name}”: source file name is too long");
    }

    public static void Validate(MouseBackup m)
    {
        Check(m.Version == 1, $"{MouseFile} is from a newer Hub (version {m.Version})");
        CheckName(m.Model, "The mouse model", max: 100);
        Check(m.DpiStages is not null && m.Buttons is not null, $"{MouseFile} is missing a section");
        Check(m.DpiStages!.Count <= 5 && m.DpiStages.All(d => d is >= 100 and <= 35000), "DPI stages must be 1 to 5 values of 100..35000");
        Check(m.DpiStages.Count == 0 || m.ActiveStage >= 1 && m.ActiveStage <= m.DpiStages.Count, "The active DPI stage is out of range");
        Check(m.PollingHz is null or 125 or 250 or 500 or 1000 or 2000 or 4000 or 8000, $"Polling rate {m.PollingHz} Hz isn't one a mouse uses");
        Check(m.IdleMinutes is null or >= 1 and <= 15, "Sleep timer must be 1..15 minutes");
        Check(m.LowBatteryPercent is null or >= 5 and <= 100, "Low battery level must be 5..100%");
        if (m.Lift is { } l)
            Check(l.Tracking is >= 1 and <= 3 && (!l.Asymmetric || l.LiftOff is >= 2 and <= 26 && l.Landing >= 1 && l.Landing < l.LiftOff),
                "Lift-off distances are out of range");
        if (m.DynamicSensitivity is { } d)
        {
            Check(d.Mode is >= 0 and <= 3, "Unknown Dynamic Sensitivity mode");
            Check(d.Points is not null && (d.Points.Count == 0 || IsCurve(d.Points)), "The Dynamic Sensitivity curve isn't one Hub's editor makes");
        }
        if (m.Rotation is { } r) Check(r.Angle is >= -44 and <= 44, "Rotation must be -44..44 degrees");
        Check(m.Buttons!.Count <= 32, "Too many buttons");
        foreach (var (id, b) in m.Buttons)
        {
            Check(byte.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var bid) && bid != 1, $"Button “{id}” can't be set");
            Check(b is not null && b.Data is not null && IsButton(b), $"Button {id}'s assignment is invalid");
        }
    }

    /// <summary>The curve rules of Hub's editor: from x 0 to 100, x rising, speed factor 0.1..2, and one the mouse can sample.</summary>
    public static bool IsCurve(List<double[]> points)
    {
        if (points.Count is < 3 or > 32 || points.Any(p => p is not { Length: 2 } || !Finite(p[0]) || !Finite(p[1]) || p[1] is < 0.1 or > 2)) return false;
        if (points[0][0] != 0 || points[^1][0] != 100) return false;
        for (int i = 1; i < points.Count; i++) if (points[i][0] <= points[i - 1][0]) return false;
        try { return Razer.SensitivityCurve.Sample(points.Select(p => new Razer.CurvePoint(p[0], p[1])).ToList()).All(double.IsFinite); }
        catch (Exception) { return false; }
    }

    /// <summary>A well-formed assignment: the shapes Hub and the vendor app write for each function, at most 5 data bytes.</summary>
    public static bool IsButton(ButtonBackup b)
    {
        if (b.Data.Length > 5 || b.Data.Any(x => x is < 0 or > 255) || b.Function is < 0 or > 255) return false;
        return (Razer.ButtonFunction)b.Function switch
        {
            Razer.ButtonFunction.Off => b.Data.Length == 0,
            Razer.ButtonFunction.MouseButton or Razer.ButtonFunction.DoubleClick => b.Data is [1 or 2 or 3 or 4 or 5 or 9 or 10],
            Razer.ButtonFunction.Dpi => b.Data is [1 or 2 or 6 or 7],
            Razer.ButtonFunction.Key => b.Data is [_, >= 0x04 and <= 0xE7], // [modifiers, keyboard usage]
            Razer.ButtonFunction.Profile or Razer.ButtonFunction.MediaKey or Razer.ButtonFunction.TurboKey or Razer.ButtonFunction.TurboButton => true,
            _ => false,
        };
    }

    static void CheckName(string? name, string what, int max = 100) =>
        Check(!string.IsNullOrWhiteSpace(name) && name.Length <= max && !name.Any(char.IsControl), $"{what} has a missing or invalid name");

    static bool Finite(double d) => double.IsFinite(d);

    // ---------------- what a restore changes ----------------

    /// <summary>Plain-language lines describing what restoring <paramref name="backup"/> over <paramref name="current"/> changes.</summary>
    public static List<string> Diff(AudioBackup current, MouseBackup? currentMouse, AudioBackup backup, MouseBackup? backupMouse)
    {
        var lines = new List<string>();
        foreach (var ch in Channels)
        {
            var now = current.Profiles.GetValueOrDefault(ch) ?? [];
            var then = backup.Profiles.GetValueOrDefault(ch) ?? [];
            foreach (var p in then)
                if (now.FirstOrDefault(x => x.Name == p.Name) is not { } q) lines.Add($"{ch} profile “{p.Name}” comes back");
                else if (!SameProfile(p, q)) lines.Add($"{ch} profile “{p.Name}” goes back to its backed-up filters");
            foreach (var p in now.Where(p => !then.Any(x => x.Name == p.Name)))
                lines.Add($"{ch} profile “{p.Name}” is removed (not in this backup)");
        }
        foreach (var d in backup.Devices)
        {
            var mine = current.Devices.FirstOrDefault(x => x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var ch in Channels)
            {
                var was = mine?.Presets.GetValueOrDefault(ch);
                var will = d.Presets.GetValueOrDefault(ch);
                if (will is not null && was != will) lines.Add($"{d.Name} · {ch.ToLowerInvariant()}: {was ?? "(not set)"} → {will}");
                var tameWas = mine?.Tame.GetValueOrDefault(ch) ?? 0;
                var tameWill = d.Tame.GetValueOrDefault(ch);
                if (tameWas != tameWill) lines.Add($"{d.Name} · {ch.ToLowerInvariant()} tame: {Tame(tameWas)} → {Tame(tameWill)}");
            }
        }
        if (backup.DefaultDevice is { } def && !def.Equals(current.DefaultDevice, StringComparison.OrdinalIgnoreCase))
            lines.Add($"Default for new devices: {current.DefaultDevice ?? "(none)"} → {def}");
        foreach (var (app, ch) in backup.AppChannels)
            if (current.AppChannels.GetValueOrDefault(app) != ch) lines.Add($"{app} plays on {Title(ch)}");
        foreach (var app in current.AppChannels.Keys.Where(k => !backup.AppChannels.ContainsKey(k)))
            lines.Add($"{app} goes back to its automatic channel");
        foreach (var ch in Channels)
            if (Math.Abs(current.Volumes.GetValueOrDefault(ch, 1) - backup.Volumes.GetValueOrDefault(ch, 1)) > 0.005)
                lines.Add($"{ch} volume: {current.Volumes.GetValueOrDefault(ch, 1):P0} → {backup.Volumes.GetValueOrDefault(ch, 1):P0}");
        if (Math.Abs(current.ChatMix - backup.ChatMix) > 0.005) lines.Add("ChatMix balance changes");
        if (!current.HeadsetEq.Select(h => (h.Name, string.Join(",", h.Gains))).SequenceEqual(backup.HeadsetEq.Select(h => (h.Name, string.Join(",", h.Gains)))))
            lines.Add("Headset EQ presets go back to the backed-up list");

        if (backupMouse is not null)
        {
            if (currentMouse is null) lines.Add("Mouse settings: skipped, the mouse isn't connected");
            else if (currentMouse.Model != backupMouse.Model) lines.Add($"Mouse settings: skipped, the backup is from a {backupMouse.Model}");
            else lines.AddRange(MouseDiff(currentMouse, backupMouse));
        }
        return lines;
    }

    static IEnumerable<string> MouseDiff(MouseBackup now, MouseBackup then)
    {
        if (!now.DpiStages.SequenceEqual(then.DpiStages) || now.ActiveStage != then.ActiveStage)
            yield return $"Mouse DPI stages: {string.Join(" / ", now.DpiStages)} → {string.Join(" / ", then.DpiStages)} (stage {then.ActiveStage} active)";
        if (then.PollingHz is { } hz && now.PollingHz != hz) yield return $"Mouse polling rate: {now.PollingHz} → {hz} Hz";
        if (then.IdleMinutes is { } idle && now.IdleMinutes != idle) yield return $"Mouse sleep timer: {now.IdleMinutes} → {idle} min";
        if (then.LowBatteryPercent is { } lb && now.LowBatteryPercent != lb) yield return $"Mouse low battery level: {now.LowBatteryPercent}% → {lb}%";
        if (then.Lift is { } l && now.Lift != l) yield return "Mouse lift-off distance changes";
        if (then.DynamicSensitivity is { } d && (now.DynamicSensitivity is not { } n || n.Enabled != d.Enabled || n.Mode != d.Mode
            || !n.Points.Select(p => (p[0], p[1])).SequenceEqual(d.Points.Select(p => (p[0], p[1])))))
            yield return "Mouse Dynamic Sensitivity changes";
        if (then.Rotation is { } r && now.Rotation != r) yield return $"Mouse rotation: {(r.Enabled ? r.Angle + "°" : "off")}";
        foreach (var (id, b) in then.Buttons)
            if (now.Buttons.GetValueOrDefault(id) is not { } c || c.Function != b.Function || !c.Data.SequenceEqual(b.Data))
                yield return $"Mouse button {id} is reassigned";
    }

    static bool SameProfile(ProfileBackup a, ProfileBackup b) =>
        a.GainDb == b.GainDb && a.Limiter == b.Limiter && a.AutoHeadroom == b.AutoHeadroom && a.Filters.SequenceEqual(b.Filters);

    static string Tame(double db) => db > 0 ? $"−{db:0} dB" : "off";
    static string Title(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];

    const string Readme = """
        # Hub settings backup

        Written by Hub (Settings → Backup). Each backup replaces every file here, so keep your own files elsewhere.

        - `audio.json`: your audio profiles, each output device's Game/Chat/Media/Music choices and tame levels, the default
          device for new devices, which channel each app plays on, channel volumes, ChatMix and the headset's EQ presets.
        - `mouse.json`: the mouse's onboard settings: DPI stages, polling rate, sleep timer, lift-off, Dynamic
          Sensitivity, rotation and button assignments.
        - `profiles/<channel>/*.txt`: your own profiles as Equalizer APO presets.
        - `in use/*.txt`: exactly what each device plays on each channel, built-in presets included.

        The `.txt` files are Equalizer APO presets: copy one into `C:\Program Files\EqualizerAPO\config` and add
        `Include: <file name>` to `config.txt`, or load it in Peace or any other tool that reads Equalizer APO presets.
        Hub designs its filters at 96 kHz, so very sharp filters above about 10 kHz can sound slightly different at
        other sample rates.

        To restore, open Hub → Settings → Backup → Restore. Hub only reads `audio.json` and `mouse.json`.

        """;
}
