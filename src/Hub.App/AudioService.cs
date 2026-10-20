using Hub.App.Native;
using Hub.Core.Audio;
using Microsoft.Win32;

namespace Hub.App;

/// <summary>
/// Game/Chat/Media audio channels: VoiceMeeter Potato virtual inputs (strips 5/6/7) routed to the chosen output,
/// each with an Equalizer APO config generated from its preset. Volume × ChatMix → strip faders.
/// </summary>
public sealed class AudioService : IDisposable
{
    public sealed record ChannelDef(AudioChannel Channel, string Id, string Name, int Strip, string EndpointName);

    public static readonly ChannelDef[] Channels =
    [
        new(AudioChannel.Game, "game", "Game", 5, "Voicemeeter Input"),
        new(AudioChannel.Chat, "chat", "Chat", 6, "Voicemeeter AUX Input"),
        new(AudioChannel.Media, "media", "Media", 7, "Voicemeeter VAIO3 Input"),
    ];

    /// <summary>
    /// HUB_DRY_RUN=1: never touch Windows audio, VoiceMeeter or Equalizer APO (development and screenshots while
    /// another Hub owns the real setup). Reads still work.
    /// </summary>
    public static readonly bool DryRun = Environment.GetEnvironmentVariable("HUB_DRY_RUN") == "1";

    readonly AppSettings _settings;
    readonly Dictionary<AudioChannel, ChannelPresets> _presets;
    VoiceMeeter? _vm;

    public AudioService(AppSettings settings)
    {
        _settings = settings;
        _presets = BuiltInPresets.All.ToDictionary(c => c.Channel);
    }

    // ---------------- status ----------------

    public sealed record Status(bool VoiceMeeter, bool Eapo, bool Routed, List<string> Steps);

    static string? EapoConfigDir()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
        var dir = key?.GetValue("ConfigPath") as string
                  ?? (key?.GetValue("InstallPath") is string install ? Path.Combine(install, "config") : null);
        return dir is not null && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// The VoiceMeeter input carrying a channel. Found by its original name the first time, then remembered by GUID,
    /// so it survives being renamed to "Game"/"Chat"/"Media".
    /// </summary>
    AudioEndpoint? Endpoint(List<AudioEndpoint> all, ChannelDef c)
    {
        if (_settings.ChannelEndpoints.TryGetValue(c.Id, out var guid)
            && all.FirstOrDefault(e => e.IsRender && e.Guid == guid) is { } known)
            return known.Active ? known : null;
        var found = all.FirstOrDefault(e => e.IsRender && e.Active && e.Name.StartsWith(c.EndpointName + " (", StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            _settings.ChannelEndpoints[c.Id] = found.Guid;
            _settings.Save();
        }
        return found;
    }

    public Status GetStatus()
    {
        var endpoints = AudioEndpoints.List();
        bool vm = VoiceMeeter.IsPotatoInstalled() && Channels.All(c => Endpoint(endpoints, c) is not null);
        bool eapoInstalled = EapoConfigDir() is not null;
        bool eapoOnInputs = eapoInstalled && Channels.All(c => Endpoint(endpoints, c)?.HasEqualizerApo == true);
        var steps = new List<string>();
        if (!vm) steps.Add("Install VoiceMeeter Potato (vb-audio.com/Voicemeeter/potato.htm) as administrator, then reboot.");
        if (!eapoInstalled) steps.Add("Install Equalizer APO (sourceforge.net/projects/equalizerapo).");
        if (eapoInstalled && !eapoOnInputs)
            steps.Add("In Equalizer APO's Configurator tick Voicemeeter Input, Voicemeeter AUX Input and Voicemeeter VAIO3 Input, then reboot.");
        if (vm && !_settings.AudioRouted) steps.Add("Press “Configure routing” below.");
        return new Status(vm, eapoOnInputs, vm && _settings.AudioRouted, steps);
    }

    // ---------------- presets ----------------

    /// <summary>The preset a channel starts on.</summary>
    public string? DefaultChoice(AudioChannel ch) => _presets.GetValueOrDefault(ch)?.DefaultName;

    /// <summary>The read-only built-in presets for a channel.</summary>
    public IReadOnlyList<EqPresetData> Presets(AudioChannel ch) => _presets.TryGetValue(ch, out var p) ? p.Presets : [];

    public List<CustomAudioPreset> CustomPresets(AudioChannel ch) =>
        _settings.CustomAudioPresets.TryGetValue(ch.ToString(), out var list) ? list : [];

    /// <summary>
    /// How a channel is rendered: Game/Media run the EQ at full gain into the look-ahead limiter at 96 kHz (more
    /// headroom for the filters and limiter); Chat stays at 48 kHz with automatic headroom.
    /// </summary>
    public sealed record Chain(bool Limiter, bool AutoHeadroom, int SampleRate);

    public static Chain DefaultChain(AudioChannel ch) =>
        ch == AudioChannel.Chat ? new(false, true, 48000) : new(true, true, HighSampleRate);

    public static readonly int[] SampleRates = [44100, 48000, 96000, 192000];

    string ActiveName(AudioChannel ch) =>
        _settings.AudioPresets.GetValueOrDefault(ch.ToString()) ?? _presets.GetValueOrDefault(ch)?.DefaultName ?? "Flat";

    CustomAudioPreset? ActiveCustom(AudioChannel ch) => CustomPresets(ch).FirstOrDefault(p => p.Name == ActiveName(ch));

    public bool IsCustomActive(AudioChannel ch) => ActiveCustom(ch) is not null;

    public EqPresetData ActivePreset(AudioChannel ch)
    {
        if (ActiveCustom(ch) is { } c) return new EqPresetData(c.Name, c.GainDb, c.Filters, "Custom");
        var name = ActiveName(ch);
        return Presets(ch).FirstOrDefault(p => p.Name == name) ?? EqPresetData.Flat();
    }

    public Chain ActiveChain(AudioChannel ch) =>
        ActiveCustom(ch) is { } c ? new(c.Limiter, c.AutoHeadroom, c.SampleRate) : DefaultChain(ch);

    public void SetPreset(AudioChannel ch, string name)
    {
        if (!Presets(ch).Any(p => p.Name == name) && !CustomPresets(ch).Any(p => p.Name == name))
            throw new ArgumentException($"No preset “{name}”");
        _settings.AudioPresets[ch.ToString()] = name;
        _settings.Save();
        ApplyAll();
    }

    /// <summary>
    /// Creates or replaces a custom profile (<paramref name="originalName"/> = the one being edited, null for a new
    /// copy) and makes it the channel's active preset.
    /// </summary>
    public void SaveCustom(AudioChannel ch, string? originalName, CustomAudioPreset preset)
    {
        preset.Name = preset.Name.Trim();
        if (preset.Name.Length == 0) throw new ArgumentException("Give the profile a name");
        if (Presets(ch).Any(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"“{preset.Name}” is a built-in preset name; pick another");
        var list = CustomPresets(ch);
        if (list.Any(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase) && p.Name != originalName))
            throw new ArgumentException($"A custom profile called “{preset.Name}” already exists");
        if (!SampleRates.Contains(preset.SampleRate)) throw new ArgumentException($"Unsupported sample rate {preset.SampleRate}");
        if (preset.Filters.Count > 20) throw new ArgumentException("At most 20 filters");
        foreach (var f in preset.Filters)
        {
            if (f.Frequency < 10 || f.Frequency >= preset.SampleRate / 2.0) throw new ArgumentException($"Filter frequency {f.Frequency} Hz is out of range");
            if (f.Q is < 0.05 or > 50) throw new ArgumentException($"Q {f.Q} is out of range (0.05–50)");
            if (Math.Abs(f.GainDb) > 24) throw new ArgumentException($"Filter gain {f.GainDb} dB is out of range (±24)");
        }
        if (Math.Abs(preset.GainDb) > 24) throw new ArgumentException("Gain is out of range (±24 dB)");

        int at = originalName is null ? -1 : list.FindIndex(p => p.Name == originalName);
        if (at >= 0) list[at] = preset; else list.Add(preset);
        _settings.CustomAudioPresets[ch.ToString()] = list;
        _settings.AudioPresets[ch.ToString()] = preset.Name;
        _settings.Save();
        ApplyAll();
    }

    /// <summary>Removes a custom profile; if it was active the channel goes back to its default preset.</summary>
    public void DeleteCustom(AudioChannel ch, string name)
    {
        var list = CustomPresets(ch);
        if (list.RemoveAll(p => p.Name == name) == 0) throw new ArgumentException($"No custom profile “{name}”");
        if (_settings.AudioPresets.GetValueOrDefault(ch.ToString()) == name) _settings.AudioPresets.Remove(ch.ToString());
        _settings.Save();
        ApplyAll();
    }

    void ApplyAll()
    {
        WriteEqualizerApo();
        ApplySampleRates();
    }

    /// <summary>
    /// Game and Media devices run at 96 kHz by default, so EQ and limiter run there too: filter shapes near the top of
    /// the audible band stay accurate and the limiter sees inter-sample peaks. Apps' 48 kHz audio is upsampled by Windows.
    /// </summary>
    public const int HighSampleRate = 96000;

    /// <summary>Puts each channel's Windows device at its active profile's rate (no-op when already there).</summary>
    public void ApplySampleRates()
    {
        if (DryRun) return;
        var endpoints = AudioEndpoints.List();
        foreach (var c in Channels)
            if (Endpoint(endpoints, c) is { } ep) AudioEndpoints.SetSampleRate(ep.Id, ActiveChain(c.Channel).SampleRate);
    }

    /// <summary>Writes hub\{game,chat,media}.txt and the Device-scoped include block in config.txt.</summary>
    public void WriteEqualizerApo()
    {
        if (DryRun) return;
        var dir = EapoConfigDir();
        if (dir is null) return;
        var endpoints = AudioEndpoints.List();
        Directory.CreateDirectory(Path.Combine(dir, "hub"));
        var limiter = InstallLimiter(dir);
        foreach (var c in Channels)
        {
            var chain = ActiveChain(c.Channel);
            File.WriteAllText(Path.Combine(dir, "hub", $"{c.Id}.txt"),
                ActivePreset(c.Channel).ToEqualizerApo(chain.Limiter ? limiter : null, chain.AutoHeadroom, chain.SampleRate));
        }

        var config = Path.Combine(dir, "config.txt");
        var backup = config + ".before-hub";
        if (File.Exists(config) && !File.Exists(backup)) File.Copy(config, backup);
        var lines = new List<string> { "# Managed by Hub — Game/Chat/Media EQ. Edits here are overwritten." };
        foreach (var c in Channels)
        {
            if (Endpoint(endpoints, c) is not { } ep) continue;
            lines.Add($"Device: {ep.Guid}");
            lines.Add($@"Include: hub\{c.Id}.txt");
        }
        File.WriteAllLines(config, lines);
    }

    /// <summary>
    /// Copies hub-limiter.dll next to the EQ files. audiodg runs as LOCAL SERVICE and can't read the user profile,
    /// so the plugin must live under Program Files. Returns the path EAPO's config should reference, or null.
    /// </summary>
    static string? InstallLimiter(string configDir)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "hub-limiter.dll");
        if (!File.Exists(source)) return null;
        var target = Path.Combine(configDir, "hub", "hub-limiter.dll");
        try
        {
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                File.Copy(source, target, overwrite: true);
        }
        catch (IOException) when (File.Exists(target))
        {
            // in use by audiodg; the loaded copy keeps working and is replaced on the next write
        }
        return target;
    }

    // ---------------- mixer ----------------

    VoiceMeeter Vm() => _vm ??= new VoiceMeeter();

    public double Volume(AudioChannel ch) => _settings.AudioVolumes.GetValueOrDefault(ch.ToString(), 1.0);

    public void SetVolume(AudioChannel ch, double value)
    {
        _settings.AudioVolumes[ch.ToString()] = Math.Clamp(value, 0, 1);
        _settings.Save();
        ApplyFaders();
    }

    public void SetChatMix(double balance)
    {
        _settings.ChatMix = Math.Clamp(balance, -1, 1);
        _settings.Save();
        ApplyFaders();
    }

    public void ApplyFaders()
    {
        if (DryRun) return;
        if (!GetStatus().Routed) return;
        var (game, chat) = ChatMix.Gains(_settings.ChatMix);
        var vm = Vm();
        foreach (var c in Channels)
        {
            double mix = c.Channel switch { AudioChannel.Game => game, AudioChannel.Chat => chat, _ => 1 };
            vm.Set($"Strip[{c.Strip}].Gain", (float)ChatMix.ToFaderDb(Volume(c.Channel) * mix));
        }
    }

    /// <summary>
    /// One-time routing: VoiceMeeter A1 = the chosen output (Windows' current default by default), virtual strips 5–7 → A1 only, labelled; Windows default playback
    /// = Game, default communications = Chat (so voice apps land on Chat). Then EQ files and faders.
    /// </summary>
    public void ConfigureRouting()
    {
        if (DryRun) return;
        var vm = Vm();
        if (vm.Type != VoiceMeeter.PotatoType)
            throw new InvalidOperationException("The running VoiceMeeter is not Potato; close it and start Voicemeeter Potato");
        var (driver, output) = ResolveOutput(vm);
        _settings.AudioOutput = output; // remember it: after routing, Windows' default is Hub's own Game device
        var script = new List<string> { $"Bus[0].device.{driver}=\"{output}\";" };
        foreach (var c in Channels)
        {
            script.Add($"Strip[{c.Strip}].Label=\"{c.Name}\";");
            script.Add($"Strip[{c.Strip}].A1=1;");
            for (int bus = 2; bus <= 5; bus++) script.Add($"Strip[{c.Strip}].A{bus}=0;");
            for (int bus = 1; bus <= 3; bus++) script.Add($"Strip[{c.Strip}].B{bus}=0;");
            script.Add($"Strip[{c.Strip}].Mute=0;");
        }
        vm.Script(string.Join("\n", script));

        var endpoints = AudioEndpoints.List();
        if (Endpoint(endpoints, Channels[0]) is { } game)
            AudioEndpoints.SetDefault(game.Id, AudioEndpoints.Role.Console, AudioEndpoints.Role.Multimedia);
        if (Endpoint(endpoints, Channels[1]) is { } chat)
            AudioEndpoints.SetDefault(chat.Id, AudioEndpoints.Role.Communications);
        // The mic goes straight from the device (not through VoiceMeeter), so its own noise reduction applies.
        if (Mic(endpoints) is { } mic)
            AudioEndpoints.SetDefault(mic.Id, AudioEndpoints.Role.Console, AudioEndpoints.Role.Multimedia, AudioEndpoints.Role.Communications);

        _settings.AudioRouted = true;
        _settings.Save();
        ApplySampleRates();
        WriteEqualizerApo();
        ApplyFaders();
        RenameChannelDevices();
    }

    // ---------------- uninstall ----------------

    /// <summary>
    /// Undoes Hub's changes to Windows audio (run by the uninstaller): Equalizer APO's config.txt goes back to the
    /// copy saved before Hub's first write, hidden devices are shown again and the channel devices get their
    /// VoiceMeeter names back. VoiceMeeter's own routing and the default devices are left as they are.
    /// </summary>
    public List<string> Cleanup()
    {
        var done = new List<string>();
        if (DryRun) return done;
        if (EapoConfigDir() is { } dir)
        {
            var config = Path.Combine(dir, "config.txt");
            var backup = config + ".before-hub";
            if (File.Exists(backup)) { File.Copy(backup, config, overwrite: true); File.Delete(backup); done.Add("Equalizer APO config restored"); }
            else if (File.Exists(config) && File.ReadAllText(config).Contains("# Managed by Hub")) { File.WriteAllText(config, ""); done.Add("Equalizer APO config cleared"); }
            var hubDir = Path.Combine(dir, "hub");
            foreach (var f in Directory.Exists(hubDir) ? Directory.GetFiles(hubDir) : [])
                try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the limiter DLL stays until the audio engine restarts */ }
        }
        if (_settings.HiddenEndpoints.Count > 0) { ShowHiddenDevices(); done.Add("hidden sound devices shown"); }
        var endpoints = AudioEndpoints.List();
        foreach (var c in Channels)
            if (Endpoint(endpoints, c) is { } ep && !ep.Name.StartsWith(c.EndpointName + " (", StringComparison.Ordinal))
            {
                AudioEndpoints.Rename(ep.Id, c.EndpointName);
                done.Add($"{c.Name} renamed back to {c.EndpointName}");
            }
        _settings.AudioRouted = false;
        _settings.Save();
        return done;
    }

    // ---------------- output / mic choice ----------------

    /// <summary>VoiceMeeter driver types → the Bus[0].device.* parameter. WDM first: it bypasses Windows effects (measured).</summary>
    static readonly (int Type, string Param)[] Drivers = [(3, "wdm"), (4, "ks"), (1, "mme"), (5, "asio")];

    static bool IsVirtual(string name) =>
        name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>Physical outputs VoiceMeeter can send A1 to (each name once).</summary>
    public List<string> OutputChoices() =>
        Vm().OutputDevices.Where(d => !IsVirtual(d.Name) && Drivers.Any(x => x.Type == d.DriverType))
            .Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The chosen output on the best driver it is available with. With nothing chosen yet, Hub takes the device
    /// Windows was playing to before routing (that is where the user's headphones are) and remembers it.
    /// </summary>
    (string Driver, string Name) ResolveOutput(VoiceMeeter vm)
    {
        var wanted = _settings.AudioOutput ?? WindowsDefaultOutputName();
        var match = vm.OutputDevices
            .Where(d => wanted is not null && d.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .Select(d => (Rank: Array.FindIndex(Drivers, x => x.Type == d.DriverType), d.Name))
            .Where(d => d.Rank >= 0)
            .OrderBy(d => d.Rank)
            .FirstOrDefault();
        if (match.Name is null)
            throw new InvalidOperationException(wanted is null
                ? "Pick your headphones or speakers under Output & mic on the Audio page, then configure routing"
                : $"VoiceMeeter can't see “{wanted}” — turn it on or pick another output");
        return (Drivers[match.Rank].Param, match.Name);
    }

    /// <summary>Friendly name of Windows' default playback device, unless that is already one of VoiceMeeter's.</summary>
    static string? WindowsDefaultOutputName()
    {
        var id = AudioEndpoints.GetDefault(render: true);
        var name = AudioEndpoints.List().FirstOrDefault(e => e.Id == id)?.Name;
        return name is null || IsVirtual(name) ? null : name;
    }

    /// <summary>The output Hub routes to right now (what ResolveOutput would pick), or null.</summary>
    public string? CurrentOutput()
    {
        try { return ResolveOutput(Vm()).Name; } catch (Exception) { return _settings.AudioOutput; }
    }

    public void SetOutput(string? name)
    {
        _settings.AudioOutput = string.IsNullOrWhiteSpace(name) ? null : name;
        _settings.Save();
        if (_settings.AudioRouted) ConfigureRouting();
    }

    public List<AudioEndpoint> MicChoices() => AudioEndpoints.List().Where(e => !e.IsRender && e.Active && !IsVirtual(e.Name)).ToList();

    /// <summary>The mic the user picked; null leaves Windows' default microphone alone.</summary>
    AudioEndpoint? Mic(List<AudioEndpoint> endpoints) =>
        _settings.MicEndpoint is { } id ? endpoints.FirstOrDefault(e => e.Id == id && e.Active) : null;

    public string? CurrentMic() => Mic(AudioEndpoints.List())?.Id ?? AudioEndpoints.GetDefault(render: false, AudioEndpoints.Role.Communications);

    public void SetMic(string? id)
    {
        _settings.MicEndpoint = string.IsNullOrWhiteSpace(id) ? null : id;
        _settings.Save();
        if (!DryRun && Mic(AudioEndpoints.List()) is { } mic)
            AudioEndpoints.SetDefault(mic.Id, AudioEndpoints.Role.Console, AudioEndpoints.Role.Multimedia, AudioEndpoints.Role.Communications);
    }

    // ---------------- device tidy-up ----------------

    /// <summary>Renames the three VoiceMeeter inputs to "Game", "Chat" and "Media" so they are obvious everywhere in Windows.</summary>
    public void RenameChannelDevices()
    {
        if (DryRun) return;
        var endpoints = AudioEndpoints.List();
        foreach (var c in Channels)
            if (Endpoint(endpoints, c) is { } ep && !ep.Name.StartsWith(c.Name + " (", StringComparison.Ordinal))
                AudioEndpoints.Rename(ep.Id, c.Name);
    }

    /// <summary>VoiceMeeter's extra endpoints (In 1–5, Out A1–B3) that Hub doesn't use.</summary>
    public List<AudioEndpoint> UnusedDevices()
    {
        var endpoints = AudioEndpoints.List();
        var used = Channels.Select(c => Endpoint(endpoints, c)?.Guid).ToHashSet();
        return endpoints.Where(e => e.Active && !used.Contains(e.Guid)
            && e.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public int HideUnusedDevices()
    {
        if (DryRun) return 0;
        var unused = UnusedDevices();
        foreach (var e in unused)
        {
            AudioEndpoints.SetVisible(e.Id, false);
            if (!_settings.HiddenEndpoints.Contains(e.Id)) _settings.HiddenEndpoints.Add(e.Id);
        }
        _settings.Save();
        return unused.Count;
    }

    public void ShowHiddenDevices()
    {
        if (DryRun) return;
        foreach (var id in _settings.HiddenEndpoints) AudioEndpoints.SetVisible(id, true);
        _settings.HiddenEndpoints.Clear();
        _settings.Save();
    }

    // ---------------- per-app routing ----------------

    public sealed record AppRoute(string Name, string? Channel, bool Playing);

    static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase) { "Hub", "audiodg", "voicemeeter8x64", "voicemeeter8" };

    /// <summary>Apps with an audio session right now, and which channel (or other device) they play on.</summary>
    public List<AppRoute> Apps()
    {
        var endpoints = AudioEndpoints.List();
        var byGuid = Channels.Select(c => (c.Id, Endpoint(endpoints, c)?.Guid)).Where(x => x.Guid is not null).ToDictionary(x => x.Guid!, x => x.Id);
        return AudioSessions.List()
            .Where(s => !IgnoredProcesses.Contains(s.ProcessName))
            .GroupBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppRoute(g.Key,
                g.Select(s => byGuid.GetValueOrDefault(s.EndpointGuid)).FirstOrDefault(ch => ch is not null),
                g.Any(s => s.Active)))
            .OrderByDescending(a => a.Playing).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Sends every running instance of an app to a channel; Windows remembers the choice for that app.</summary>
    public void AssignApp(string processName, string channelId)
    {
        if (DryRun) return;
        var endpoints = AudioEndpoints.List();
        var target = Endpoint(endpoints, Channels.First(c => c.Id == channelId)) ?? throw new InvalidOperationException("Channel device missing");
        var pids = AudioSessions.List().Where(s => s.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)).Select(s => s.ProcessId).Distinct().ToList();
        if (pids.Count == 0) throw new InvalidOperationException($"{processName} isn't playing audio right now");
        // Game is the system default; clearing the per-app override keeps the app following the default.
        foreach (var pid in pids) AudioSessions.SetAppOutput(pid, channelId == "game" ? null : target.Id);
    }

    public string Summary()
    {
        var s = GetStatus();
        if (!s.Routed) return "Audio setup not finished — open the Audio page.";
        return string.Join(" · ", Channels.Select(c => $"{c.Name}: {ActivePreset(c.Channel).Name}"));
    }

    public void Dispose() => _vm?.Dispose();
}
