using System.Text.Json;
using Hub.Core.Audio;

namespace Hub.App;

public sealed class EqPreset
{
    public string Name { get; set; } = "";
    public int[] Gains { get; set; } = [0, 0, 0, 0, 0];
}

public sealed record CurvePointDto(double X, double Y);

/// <summary>A user-made audio profile: an editable copy of a built-in preset plus the render chain around it.</summary>
public sealed class CustomAudioPreset
{
    public string Name { get; set; } = "";
    /// <summary>Gain before the filters, dB.</summary>
    public double GainDb { get; set; }
    public List<EqFilter> Filters { get; set; } = [];
    /// <summary>Look-ahead limiter after the EQ.</summary>
    public bool Limiter { get; set; } = true;
    /// <summary>Without the limiter: lower the gain by the curve's peak boost so it can't clip.</summary>
    public bool AutoHeadroom { get; set; } = true;
    /// <summary>Rate the channel's Windows device (and so the EQ and limiter) runs at.</summary>
    public int SampleRate { get; set; } = 96000;
}

/// <summary>
/// App preferences. Device settings themselves live on the devices (both store them onboard);
/// this file keeps what the hardware cannot: preset libraries, audio choices and app behaviour.
/// </summary>
public sealed class AppSettings
{
    public List<EqPreset> HeadsetEqPresets { get; set; } =
    [
        // Hub's own starting points for the headset's 5 onboard bands (80 Hz, 240 Hz, 750 Hz, 2.2 kHz, 6.6 kHz).
        new() { Name = "Flat", Gains = [0, 0, 0, 0, 0] },
        new() { Name = "Footsteps", Gains = [-4, -2, -1, 4, 4] },
        new() { Name = "Voice", Gains = [-2, 0, 2, 3, 1] },
        new() { Name = "Warm", Gains = [3, 2, 0, -1, -2] },
        new() { Name = "Bass", Gains = [5, 3, 0, 0, 1] },
    ];

    /// <summary>Switch the mouse out of the vendor app's Driver mode so its DPI button works onboard.</summary>
    public bool RestoreMouseNormalMode { get; set; } = true;

    public int LowBatteryNotifyPercent { get; set; } = 20;

    /// <summary>Colour variant of the mouse picture, when the catalog has more than one.</summary>
    public string MouseColor { get; set; } = "black";

    /// <summary>Colour variant of the headset picture, when the catalog has more than one.</summary>
    public string HeadsetColor { get; set; } = "black";

    /// <summary>Custom Dynamic Sensitivity control points (a gentle ramp to start from).</summary>
    public List<CurvePointDto> CustomCurve { get; set; } =
        [new(0, 1), new(10, 1.15), new(25, 1.3), new(50, 1.4), new(100, 1.4)];

    /// <summary>Selected preset per channel (missing → the channel's default).</summary>
    public Dictionary<string, string> AudioPresets { get; set; } = [];
    /// <summary>Channel name → the user's custom profiles (built-in presets are read-only).</summary>
    public Dictionary<string, List<CustomAudioPreset>> CustomAudioPresets { get; set; } = [];
    public Dictionary<string, double> AudioVolumes { get; set; } = [];
    public double ChatMix { get; set; }
    public bool AudioRouted { get; set; }
    /// <summary>VoiceMeeter output device name for A1; null = Windows' default playback device when routing is set up.</summary>
    public string? AudioOutput { get; set; }
    /// <summary>Capture endpoint id made the default mic; null = leave Windows' default microphone alone.</summary>
    public string? MicEndpoint { get; set; }
    /// <summary>Channel id → endpoint GUID of its VoiceMeeter input (kept so renaming the device doesn't lose it).</summary>
    public Dictionary<string, string> ChannelEndpoints { get; set; } = [];
    /// <summary>Endpoint ids Hub disabled via "Hide unused devices", so they can be shown again.</summary>
    public List<string> HiddenEndpoints { get; set; } = [];

    /// <summary>%APPDATA%\Hub, or HUB_DATA_DIR (portable installs, development, screenshots).</summary>
    public static string DataDir => Environment.GetEnvironmentVariable("HUB_DATA_DIR") is { Length: > 0 } dir
        ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hub");

    static string FilePath => Path.Combine(DataDir, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
