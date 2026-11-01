namespace Hub.Core.Audio;

/// <summary>One output device's choices: the profile per channel and how much its limiter tames loud sounds.</summary>
public sealed class DeviceAudioSetup
{
    /// <summary>The device's name when it was last seen, for listing it while it's unplugged.</summary>
    public string Name { get; set; } = "";
    /// <summary>Channel ("Game"/"Chat"/"Media"/"Music") → profile name; missing = the channel's default preset.</summary>
    public Dictionary<string, string> Presets { get; set; } = [];
    /// <summary>Channel → dB below full scale the limiter holds loud peaks; missing = off.</summary>
    public Dictionary<string, double> Tame { get; set; } = [];

    public DeviceAudioSetup CopyAs(string name) => new() { Name = name, Presets = new(Presets), Tame = new(Tame) };
}

/// <summary>
/// Per-output-device audio setups. A device used for the first time starts as a copy of the default device's setup
/// (or, before there is one, of the single setup Hub had before devices got their own).
/// </summary>
public sealed class DeviceSetups(IDictionary<string, DeviceAudioSetup> devices, string? defaultId, DeviceAudioSetup legacy)
{
    public string? DefaultId { get; private set; } = defaultId;

    public IDictionary<string, DeviceAudioSetup> Devices => devices;

    /// <summary>Key of a restored setup whose device Windows hasn't shown yet; it moves to the device's id when it appears.</summary>
    public const string ByNamePrefix = "name:";

    /// <summary>The device's setup, created on first use. Keeps the stored name current.</summary>
    public DeviceAudioSetup For(string id, string name)
    {
        if (!devices.TryGetValue(id, out var setup) && name.Length > 0
            && devices.Keys.FirstOrDefault(k => Same(k, ByNamePrefix + name)) is { } waiting && devices.Remove(waiting, out var restored))
        {
            devices[id] = setup = restored;
            if (DefaultId == waiting) DefaultId = id;
        }
        else if (setup is null)
        {
            var template = DefaultId is { } d && devices.TryGetValue(d, out var def) ? def : legacy;
            devices[id] = setup = template.CopyAs(name);
            if (DefaultId is null || !devices.ContainsKey(DefaultId)) DefaultId = id;
        }
        if (name.Length > 0) setup.Name = name;
        return setup;
    }

    public void StartFlat(string id, IEnumerable<string> channels)
    {
        var setup = Get(id);
        setup.Presets = channels.ToDictionary(c => c, _ => "Flat");
        setup.Tame.Clear();
    }

    public void MakeDefault(string id)
    {
        Get(id);
        DefaultId = id;
    }

    public void RenameProfile(string channel, string oldName, string newName)
    {
        foreach (var s in devices.Values)
            if (s.Presets.GetValueOrDefault(channel) == oldName) s.Presets[channel] = newName;
    }

    /// <summary>Devices using a deleted profile go back to the channel's default preset.</summary>
    public void RemoveProfile(string channel, string name)
    {
        foreach (var s in devices.Values)
            if (s.Presets.GetValueOrDefault(channel) == name) s.Presets.Remove(channel);
    }

    /// <summary>
    /// Puts back a backup's device setups. A backup names devices, not ids (Windows can list one device under several):
    /// each goes to every setup Hub has for a device of that name and every Windows device of that name, else waits
    /// under its name until one appears. Devices the backup doesn't mention keep their setup.
    /// </summary>
    public void Restore(IEnumerable<Backup.DeviceBackup> backup, string? defaultName, IReadOnlyCollection<(string Id, string Name)> endpoints)
    {
        foreach (var d in backup)
        {
            var ids = devices.Where(kv => Same(kv.Value.Name, d.Name) || Same(kv.Key, ByNamePrefix + d.Name)).Select(kv => kv.Key)
                .Concat(endpoints.Where(e => Same(e.Name, d.Name)).Select(e => e.Id))
                .Distinct().ToList();
            if (ids.Count > 1) ids.RemoveAll(id => id.StartsWith(ByNamePrefix, StringComparison.Ordinal) && devices.Remove(id));
            if (ids.Count == 0) ids.Add(ByNamePrefix + d.Name);
            foreach (var id in ids) devices[id] = new DeviceAudioSetup { Name = d.Name, Presets = new(d.Presets), Tame = new(d.Tame) };
            if (defaultName is not null && Same(d.Name, defaultName) && (DefaultId is null || !ids.Contains(DefaultId))) DefaultId = ids[0];
        }
    }

    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    DeviceAudioSetup Get(string id) =>
        devices.TryGetValue(id, out var s) ? s : throw new ArgumentException("Hub hasn't played to that device yet");
}
