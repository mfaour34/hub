using System.Text.Json;
using System.Text.Json.Serialization;
using Hub.Core.Audio;

namespace Hub.Core.Backup;

/// <summary>One custom audio profile as stored in a backup.</summary>
public sealed record ProfileBackup(string Name, double GainDb, List<EqFilter> Filters, bool Limiter, bool AutoHeadroom, string? Source = null);

/// <summary>One output device's choices, by the device's name (Windows gives a device a new id on another PC or install).</summary>
public sealed record DeviceBackup(string Name, Dictionary<string, string> Presets, Dictionary<string, double> Tame);

/// <summary>A preset of the headset's own 5-band EQ (the Headset page).</summary>
public sealed record HeadsetEqBackup(string Name, int[] Gains);

/// <summary>Everything audio Hub keeps, minus what only makes sense on this PC (chosen output, mic, sink).</summary>
public sealed class AudioBackup
{
    public int Version { get; set; } = 1;
    /// <summary>Channel ("Game"/"Chat"/"Media"/"Music") → the user's own profiles.</summary>
    public Dictionary<string, List<ProfileBackup>> Profiles { get; set; } = [];
    public List<DeviceBackup> Devices { get; set; } = [];
    /// <summary>Name of the device new devices copy.</summary>
    public string? DefaultDevice { get; set; }
    /// <summary>Process name → channel id ("game"/"chat"/"media"/"music").</summary>
    public Dictionary<string, string> AppChannels { get; set; } = [];
    public Dictionary<string, double> Volumes { get; set; } = [];
    public double ChatMix { get; set; }
    public List<HeadsetEqBackup> HeadsetEq { get; set; } = [];
}

public sealed record LiftBackup(bool Asymmetric, int Tracking, int LiftOff, int Landing);
public sealed record DynamicSensitivityBackup(bool Enabled, int Mode, List<double[]> Points);
public sealed record RotationBackup(bool Enabled, int Angle);
public sealed record ButtonBackup(int Function, int[] Data);

/// <summary>The mouse's onboard settings Hub can read and write. Null parts weren't supported by the mouse.</summary>
public sealed class MouseBackup
{
    public int Version { get; set; } = 1;
    /// <summary>The mouse it came from; a restore only writes to the same model.</summary>
    public string Model { get; set; } = "";
    public List<int> DpiStages { get; set; } = [];
    public int ActiveStage { get; set; } = 1;
    public int? PollingHz { get; set; }
    public int? IdleMinutes { get; set; }
    public int? LowBatteryPercent { get; set; }
    public LiftBackup? Lift { get; set; }
    public DynamicSensitivityBackup? DynamicSensitivity { get; set; }
    public RotationBackup? Rotation { get; set; }
    /// <summary>Firmware button id → its assignment.</summary>
    public Dictionary<string, ButtonBackup> Buttons { get; set; } = [];
}

public static class BackupJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n") + "\n";

    public static T Read<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("The backup file is empty");
}
