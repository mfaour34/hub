using System.Text.Json;
using System.Text.Json.Nodes;
using Hub.Core.Aim;

namespace Hub.App.Aim;

/// <summary>The aim lab's own settings, kept apart from settings.json so the running Hub never overwrites them.</summary>
public sealed class AimSettings
{
    public string Game { get; set; } = "cs2";
    public double? Sens { get; set; }
    public double? Dpi { get; set; }
    /// <summary>The sensitivity finder's last result.</summary>
    public double? LastCm360 { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hub", "aim.json");

    public static AimSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AimSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>The aim lab page's commands (same message shape as the main window's bridge).</summary>
public sealed class AimBridge(int? dpi, bool accel, Action close)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly AimSettings _settings = AimSettings.Load();
    Finder? _finder;

    public Task<string> HandleAsync(string message) => Task.FromResult(Handle(message));

    string Handle(string message)
    {
        var msg = JsonNode.Parse(message)!;
        int id = msg["id"]!.GetValue<int>();
        try
        {
            var data = Dispatch(msg["cmd"]!.GetValue<string>(), msg["args"] as JsonObject ?? []);
            return JsonSerializer.Serialize(new { id, ok = true, data }, Json);
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { id, ok = false, error = ex.Message }, Json); }
    }

    object? Dispatch(string cmd, JsonObject a)
    {
        switch (cmd)
        {
            case "aim.init":
                return new
                {
                    // the mouse's active stage when Hub could read it; a DPI typed in here only when it couldn't
                    dpi = dpi ?? _settings.Dpi ?? 800, mouseDpi = dpi, accel,
                    games = Games.All.Select(g => new { id = g.Id, name = g.Name, yaw = g.DegPerCount(1), linear = g.Id != "pubg", defaultSens = g.DefaultSens, hfov = g.HFov169, notes = g.Notes, verified = g.Verified }),
                    settings = new { game = _settings.Game, sens = _settings.Sens, lastCm360 = _settings.LastCm360 },
                };
            case "aim.save":
                _settings.Game = a["game"]?.GetValue<string>() is { } g && Games.All.Any(x => x.Id == g) ? g : "cs2";
                _settings.Sens = Positive(a["sens"]);
                _settings.Dpi = Positive(a["dpi"]);
                if (a.ContainsKey("lastCm360")) _settings.LastCm360 = Positive(a["lastCm360"]);
                _settings.Save();
                return null;
            case "aim.convert":
            {
                var from = a["from"]!.GetValue<string>();
                double sens = a["sens"]?.GetValue<double>() ?? 0, d = a["dpi"]?.GetValue<double>() ?? 0;
                return Games.All.Select(g =>
                {
                    var s = Sensitivity.Convert(from, sens, g.Id);
                    return new { id = g.Id, name = g.Name, sens = s, cm360 = s is null || d <= 0 ? (double?)null : Sensitivity.Cm360(g, s.Value, d), verified = g.Verified, notes = g.Notes };
                });
            }
            case "aim.degPerCount": // the view's turn rate for a game and setting (handles PUBG's curve)
                return Games.Get(a["game"]!.GetValue<string>()).DegPerCount(a["sens"]!.GetValue<double>());
            case "aim.sensFor":
            {
                var game = Games.Get(a["game"]!.GetValue<string>());
                return Sensitivity.SensFor(game, a["cm360"]!.GetValue<double>(), a["dpi"]!.GetValue<double>());
            }
            case "aim.finder.start":
                _finder = new Finder(a["cm360"]!.GetValue<double>());
                return FinderState();
            case "aim.finder.record":
                (_finder ?? throw new InvalidOperationException("Finder not started")).Record(a["low"]!.GetValue<double>(), a["high"]!.GetValue<double>());
                return FinderState();
            case "aim.close":
                close();
                return null;
        }
        throw new InvalidOperationException($"Unknown command {cmd}");
    }

    object FinderState()
    {
        var pair = _finder!.NextPair();
        return new { step = _finder.Step, steps = Finder.Steps, pair = pair is { } p ? new[] { p.LowCm, p.HighCm } : null, result = _finder.ResultCm360 };
    }

    static double? Positive(JsonNode? n) => n?.GetValue<double>() is { } v && double.IsFinite(v) && v > 0 ? v : null;
}
