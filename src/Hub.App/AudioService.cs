using Hub.App.Engine;
using Hub.App.Native;
using Hub.Core.Audio;
using Hub.Core.Audio.Engine;
using Microsoft.Win32;

namespace Hub.App;

/// <summary>
/// Game/Chat/Media audio channels with no driver or third-party software: while Hub audio is on, Windows' default
/// playback devices point at a silent "sink" device, every app playing there is captured (per-process loopback),
/// mixed into Game/Chat/Media with each channel's EQ and limiter, and played to the output device by
/// <see cref="AudioEngine"/>. Turning it off (or quitting Hub) points Windows back at the output device.
/// </summary>
public sealed class AudioService : IDisposable
{
    public sealed record ChannelDef(AudioChannel Channel, string Id, string Name);

    public static readonly ChannelDef[] Channels =
    [
        new(AudioChannel.Game, "game", "Game"),
        new(AudioChannel.Chat, "chat", "Chat"),
        new(AudioChannel.Media, "media", "Media"),
    ];

    /// <summary>
    /// HUB_DRY_RUN=1: never touch Windows audio (development and screenshots while another Hub owns the real setup).
    /// Reads still work.
    /// </summary>
    public static readonly bool DryRun = Environment.GetEnvironmentVariable("HUB_DRY_RUN") == "1";

    readonly AppSettings _settings;
    readonly Dictionary<AudioChannel, ChannelPresets> _presets;
    readonly object _gate = new();
    AudioEngine? _engine;
    AppWatcher? _watcher;
    Thread? _poller;
    volatile bool _stopPolling;
    string? _sinkId;
    bool _sinkVirtual;

    public AudioService(AppSettings settings)
    {
        _settings = settings;
        _presets = BuiltInPresets.All.ToDictionary(c => c.Channel);
    }

    // ---------------- status ----------------

    /// <param name="Blocker">Why Hub audio can't run right now (null when it can).</param>
    public sealed record Status(bool Running, string? Output, string? Sink, string? Blocker, string? Problem);

    public Status GetStatus()
    {
        var endpoints = AudioEndpoints.List();
        var output = ResolveOutput(endpoints);
        var sink = ResolveSink(endpoints, output?.Id);
        string? blocker =
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? "Needs Windows 10 version 2004 or newer (per-app audio capture)."
            : OtherMixerRunning() ? "SteelSeries Sonar is running and owns the Windows sound devices. Quit it first."
            : output is null ? "No output device found. Connect your headphones or pick an output below."
            : sink is null ? "Hub needs a second playback device to use as its silent input (Steam Streaming Speakers, which Steam installs, is ideal)."
            : null;
        string? problem = _engine?.OutputError is { } e ? $"Output device: {e}" : _watcher?.LastError is { } w ? $"Couldn't capture {w}" : null;
        return new Status(_engine is not null, output?.Name, sink?.Name, blocker, problem);
    }

    static bool OtherMixerRunning() => System.Diagnostics.Process.GetProcessesByName("SteelSeriesSonar").Length > 0;

    // ---------------- presets ----------------

    /// <summary>The preset a channel starts on.</summary>
    public string? DefaultChoice(AudioChannel ch) => _presets.GetValueOrDefault(ch)?.DefaultName;

    /// <summary>The read-only built-in presets for a channel.</summary>
    public IReadOnlyList<EqPresetData> Presets(AudioChannel ch) => _presets.TryGetValue(ch, out var p) ? p.Presets : [];

    public List<CustomAudioPreset> CustomPresets(AudioChannel ch) =>
        _settings.CustomAudioPresets.TryGetValue(ch.ToString(), out var list) ? list : [];

    /// <summary>
    /// How a channel is rendered: Game/Media run the EQ at full gain into the look-ahead limiter; Chat gets
    /// automatic headroom instead. Every channel is processed at 96 kHz (see <see cref="AudioEngine.DspRate"/>).
    /// </summary>
    public sealed record Chain(bool Limiter, bool AutoHeadroom, int SampleRate);

    public static Chain DefaultChain(AudioChannel ch) =>
        ch == AudioChannel.Chat ? new(false, true, AudioEngine.DspRate) : new(true, true, AudioEngine.DspRate);

    /// <summary>Hub's engine runs every channel's EQ at 96 kHz; profiles from Hub 1.0 may name another rate.</summary>
    public static readonly int[] SampleRates = [AudioEngine.DspRate];

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
        ActiveCustom(ch) is { } c ? new(c.Limiter, c.AutoHeadroom, AudioEngine.DspRate) : DefaultChain(ch);

    public void SetPreset(AudioChannel ch, string name)
    {
        if (!Presets(ch).Any(p => p.Name == name) && !CustomPresets(ch).Any(p => p.Name == name))
            throw new ArgumentException($"No preset “{name}”");
        _settings.AudioPresets[ch.ToString()] = name;
        _settings.Save();
        ApplyBuses();
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
        preset.SampleRate = AudioEngine.DspRate;
        if (preset.Filters.Count > 20) throw new ArgumentException("At most 20 filters");
        foreach (var f in preset.Filters)
        {
            if (f.Frequency < 10 || f.Frequency >= 24000) throw new ArgumentException($"Filter frequency {f.Frequency} Hz is out of range");
            if (f.Q is < 0.05 or > 50) throw new ArgumentException($"Q {f.Q} is out of range (0.05–50)");
            if (Math.Abs(f.GainDb) > 24) throw new ArgumentException($"Filter gain {f.GainDb} dB is out of range (±24)");
        }
        if (Math.Abs(preset.GainDb) > 24) throw new ArgumentException("Gain is out of range (±24 dB)");

        int at = originalName is null ? -1 : list.FindIndex(p => p.Name == originalName);
        if (at >= 0) list[at] = preset; else list.Add(preset);
        _settings.CustomAudioPresets[ch.ToString()] = list;
        _settings.AudioPresets[ch.ToString()] = preset.Name;
        _settings.Save();
        ApplyBuses();
    }

    /// <summary>Removes a custom profile; if it was active the channel goes back to its default preset.</summary>
    public void DeleteCustom(AudioChannel ch, string name)
    {
        var list = CustomPresets(ch);
        if (list.RemoveAll(p => p.Name == name) == 0) throw new ArgumentException($"No custom profile “{name}”");
        if (_settings.AudioPresets.GetValueOrDefault(ch.ToString()) == name) _settings.AudioPresets.Remove(ch.ToString());
        _settings.Save();
        ApplyBuses();
    }

    // ---------------- engine ----------------

    /// <summary>Turns Hub audio on and remembers it, so it comes back with Hub.</summary>
    public void Start()
    {
        if (DryRun) return;
        if (GetStatus().Blocker is { } b) throw new InvalidOperationException(b);
        // remember where the user was listening: once Hub runs, Windows' default is the sink
        _settings.AudioOutput ??= ResolveOutput(AudioEndpoints.List())?.Id;
        _settings.AudioRouted = true;
        _settings.Save();
        Resume();
    }

    /// <summary>Turns Hub audio off: Windows plays straight to the output device again.</summary>
    public void Stop()
    {
        _settings.AudioRouted = false;
        _settings.Save();
        Shutdown();
        if (!DryRun) RestoreSinkName(_settings); // also when the engine wasn't running (Hub killed while it was on)
    }

    /// <summary>Starts the engine if Hub audio is on (at Hub start-up). Does nothing while something blocks it.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (DryRun || _engine is not null || !_settings.AudioRouted) return;
            var endpoints = AudioEndpoints.List();
            var output = ResolveOutput(endpoints);
            if (output is null || OtherMixerRunning()) return;
            var sink = ResolveSink(endpoints, output.Id);
            if (sink is null) return;
            _sinkId = sink.Id;
            _sinkVirtual = IsVirtualSink(sink.Name);

            PrepareSink(sink);
            if (_sinkVirtual) NameSink(sink.Id);
            _engine = new AudioEngine(output.Id);
            ApplyBuses();
            ApplyFaders();
            _engine.Start();
            _watcher = new AppWatcher(_engine, sink.Guid, name => AppChannels.For(name, _settings.AppChannels),
                name => !IgnoredProcesses.Contains(name));
            // apps follow the Windows defaults to the sink; voice apps use the communications role
            AudioEndpoints.SetDefault(sink.Id, AudioEndpoints.Role.Console, AudioEndpoints.Role.Multimedia, AudioEndpoints.Role.Communications);
            _stopPolling = false;
            _poller = new Thread(Poll) { IsBackground = true, Name = "Hub audio apps" };
            _poller.SetApartmentState(ApartmentState.STA);
            _poller.Start();
        }
    }

    /// <summary>Stops the engine and points Windows back at the output device (Hub exiting or audio turned off).</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_engine is null) return;
            _stopPolling = true;
            _poller?.Join(3000);
            _poller = null;
            _engine.Dispose();
            _engine = null;
            _watcher = null;
            RestoreDefaults(_settings);
        }
    }

    /// <summary>
    /// Windows defaults back on the output device. Also called from the crash handler: without Hub running, apps
    /// left on the silent sink would play into nothing.
    /// </summary>
    public static void RestoreDefaults(AppSettings settings)
    {
        if (DryRun) return;
        RestoreSinkName(settings);
        var endpoints = AudioEndpoints.List();
        if (ResolveOutput(endpoints, settings) is not { } output) return;
        AudioEndpoints.SetDefault(output.Id, AudioEndpoints.Role.Console, AudioEndpoints.Role.Multimedia, AudioEndpoints.Role.Communications);
    }

    /// <summary>Gives the virtual sink Hub's name, remembering its own so <see cref="RestoreDefaults"/> can put it back.</summary>
    void NameSink(string sinkId)
    {
        try
        {
            var names = AudioEndpoints.Names(sinkId);
            if (names == (SinkDescription, SinkInterface)) return;
            if (_settings.SinkOriginalName?[0] != sinkId)
            {
                _settings.SinkOriginalName = [sinkId, names.Description, names.Interface];
                _settings.Save();
            }
            AudioEndpoints.Rename(sinkId, SinkDescription, SinkInterface);
        }
        catch (Exception) { /* cosmetic: the engine works under the device's own name */ }
    }

    /// <summary>Puts the sink's own name back (Hub audio off, Hub exiting or crashing).</summary>
    static void RestoreSinkName(AppSettings settings)
    {
        if (settings.SinkOriginalName is not [var id, var description, var iface]) return;
        try { AudioEndpoints.Rename(id, description, iface); }
        catch (Exception) { /* the device is gone; nothing to rename */ }
        settings.SinkOriginalName = null;
        settings.Save();
    }

    void Poll()
    {
        int tick = 0;
        while (!_stopPolling)
        {
            try
            {
                _watcher?.Poll();
                MirrorSinkVolume();
                // Steam may give its device its name back (Remote Play, reinstall)
                if (_sinkVirtual && _sinkId is not null && ++tick % 10 == 0) NameSink(_sinkId);
            }
            catch (Exception) { /* device churn; next poll */ }
            for (int i = 0; i < 8 && !_stopPolling; i++) Thread.Sleep(125); // once a second; stopping stays quick
        }
    }

    /// <summary>
    /// The volume keys and the Windows volume slider change the default device, which is now the sink. Its own volume
    /// doesn't reach the captured audio, so Hub applies it to the mix.
    /// </summary>
    void MirrorSinkVolume()
    {
        if (_engine is null || _sinkId is null) return;
        var vol = Wasapi.Activate<Wasapi.IAudioEndpointVolume>(Wasapi.Device(_sinkId));
        try
        {
            vol.GetMasterVolumeLevel(out float db);
            vol.GetMute(out bool mute);
            // a real device used as the sink is muted by Hub itself, so only a virtual sink's mute is the user's
            _engine.Graph.SetMasterGain(mute && _sinkVirtual ? 0 : Math.Pow(10, db / 20));
        }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(vol); }
    }

    /// <summary>Sends each channel's EQ and limiter to the engine (preset changes, start-up).</summary>
    void ApplyBuses()
    {
        if (_engine is null) return;
        foreach (var c in Channels)
        {
            var preset = ActivePreset(c.Channel);
            var chain = ActiveChain(c.Channel);
            var eq = new EqChain(preset, AudioEngine.DspRate, EqChain.Preamp(preset, chain.Limiter, chain.AutoHeadroom, AudioEngine.DspRate));
            double tame = Tame(c.Channel);
            IBlockLimiter? limiter = chain.Limiter || tame > 0 ? new NativeLimiter(AudioEngine.DspRate, Math.Pow(10, -tame / 20)) : null;
            _engine.Graph.SetBus(c.Channel, new BusSettings(eq, limiter));
        }
    }

    public const double MaxTameDb = 12;

    /// <summary>How many dB below full scale the channel's limiter holds loud peaks (gunshots, explosions).</summary>
    public double Tame(AudioChannel ch) => _settings.AudioTame.GetValueOrDefault(ch.ToString(), 0);

    public void SetTame(AudioChannel ch, double db)
    {
        db = Math.Clamp(Math.Round(db), 0, MaxTameDb);
        if (db == 0) _settings.AudioTame.Remove(ch.ToString()); else _settings.AudioTame[ch.ToString()] = db;
        _settings.Save();
        ApplyBuses();
    }

    // ---------------- mixer ----------------

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

    /// <summary>Channel volume × ChatMix as each channel's gain.</summary>
    public void ApplyFaders()
    {
        if (_engine is null) return;
        var (game, chat) = ChatMix.Gains(_settings.ChatMix);
        foreach (var c in Channels)
        {
            double mix = c.Channel switch { AudioChannel.Game => game, AudioChannel.Chat => chat, _ => 1 };
            _engine.Graph.SetBusGain(c.Channel, Volume(c.Channel) * mix);
        }
    }

    // ---------------- output, sink and mic ----------------

    // the sink renamed by Hub comes first, so a name left behind by a crash still finds the same device
    static readonly string[] VirtualSinkNames = [$"({SinkInterface})", "Steam Streaming Speakers", "Steam Streaming Microphone"];

    /// <summary>The sink's name while Hub audio is on, so Windows and apps show "Hub (Hub Audio)" as the default device.</summary>
    const string SinkDescription = "Hub", SinkInterface = "Hub Audio";

    static bool IsVirtualSink(string name) => VirtualSinkNames.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>Playback devices that are other software's virtual devices, never a place to send the mix.</summary>
    static bool IsVirtual(string name) =>
        IsVirtualSink(name) || name.Contains("Sonar", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    AudioEndpoint? ResolveOutput(List<AudioEndpoint> endpoints) => ResolveOutput(endpoints, _settings);

    /// <summary>
    /// The chosen output. With nothing chosen yet, the device Windows plays to right now (that is where the user's
    /// headphones are); <see cref="Start"/> remembers it.
    /// </summary>
    static AudioEndpoint? ResolveOutput(List<AudioEndpoint> endpoints, AppSettings settings)
    {
        var id = settings.AudioOutput ?? AudioEndpoints.GetDefault(render: true);
        return endpoints.FirstOrDefault(e => e.Id == id && e.IsRender && e.Active && !IsVirtual(e.Name));
    }

    /// <summary>
    /// The device apps play to while Hub captures them: the user's choice, else a virtual device nobody hears
    /// (Steam's streaming speakers), else any other active playback device, which Hub then mutes.
    /// </summary>
    AudioEndpoint? ResolveSink(List<AudioEndpoint> endpoints, string? outputId)
    {
        var candidates = endpoints.Where(e => e.IsRender && e.Active && e.Id != outputId).ToList();
        if (_settings.SinkEndpoint is { } id && candidates.FirstOrDefault(e => e.Id == id) is { } chosen) return chosen;
        return VirtualSinkNames.Select(v => candidates.FirstOrDefault(e => e.Name.Contains(v, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(e => e is not null)
            ?? candidates.FirstOrDefault(e => !IsVirtual(e.Name));
    }

    /// <summary>A real device used as the sink must stay silent; a virtual one is silent already.</summary>
    static void PrepareSink(AudioEndpoint sink)
    {
        if (IsVirtualSink(sink.Name)) return;
        var vol = Wasapi.Activate<Wasapi.IAudioEndpointVolume>(Wasapi.Device(sink.Id));
        try { var ctx = Guid.Empty; vol.SetMute(true, ref ctx); }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(vol); }
    }

    /// <summary>Physical outputs Hub can play to.</summary>
    public List<AudioEndpoint> OutputChoices() =>
        AudioEndpoints.List().Where(e => e.IsRender && e.Active && !IsVirtual(e.Name)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public string? CurrentOutput() => ResolveOutput(AudioEndpoints.List())?.Id;

    public void SetOutput(string? id)
    {
        _settings.AudioOutput = string.IsNullOrWhiteSpace(id) ? null : id;
        _settings.Save();
        Restart();
    }

    void Restart()
    {
        if (!_settings.AudioRouted) return;
        Shutdown();
        Resume();
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

    // ---------------- per-app channels ----------------

    /// <param name="Channel">"game"/"chat"/"media" while Hub captures it; null when it plays straight to another device.</param>
    public sealed record AppRoute(string Name, string? Channel, bool Playing);

    static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase) { "Hub", "audiodg" };

    /// <summary>Apps with an audio session right now and the channel each plays on.</summary>
    public List<AppRoute> Apps()
    {
        var endpoints = AudioEndpoints.List();
        var sinkGuid = _sinkId is null ? null : endpoints.FirstOrDefault(e => e.Id == _sinkId)?.Guid;
        return AudioSessions.List()
            .Where(s => !IgnoredProcesses.Contains(s.ProcessName))
            .GroupBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppRoute(g.Key,
                g.Any(s => s.EndpointGuid == sinkGuid) ? AppChannels.For(g.Key, _settings.AppChannels).ToString().ToLowerInvariant() : null,
                g.Any(s => s.Active)))
            .OrderByDescending(a => a.Playing).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Puts an app on a channel (remembered by process name). An app that was sent to a specific device follows the
    /// Windows default again, so it reaches Hub.
    /// </summary>
    public void AssignApp(string processName, string channelId)
    {
        if (!Channels.Any(c => c.Id == channelId)) throw new ArgumentException($"No channel “{channelId}”");
        _settings.AppChannels[processName.ToLowerInvariant()] = channelId;
        _settings.Save();
        if (DryRun) return;
        foreach (var pid in AudioSessions.List().Where(s => s.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)).Select(s => s.ProcessId).Distinct())
            if (AudioSessions.GetAppOutput(pid) is not null) AudioSessions.SetAppOutput(pid, null);
    }

    public string Summary()
    {
        if (_engine is null) return _settings.AudioRouted ? "Hub audio waiting — open the Audio page." : "Hub audio is off.";
        return string.Join(" · ", Channels.Select(c => $"{c.Name}: {ActivePreset(c.Channel).Name}"));
    }

    /// <summary>True when Hub audio is on but not running yet (e.g. the headphones aren't connected at logon).</summary>
    public bool WaitingToResume => _settings.AudioRouted && _engine is null;

    // ---------------- Hub 1.0 setup and uninstall ----------------

    /// <summary>
    /// Hub 1.0 routed the channels through VoiceMeeter Potato and Equalizer APO. Undoes what it changed, once, the
    /// first time this version runs with such settings: the engine replaces all of it.
    /// </summary>
    public void MigrateFromVoiceMeeter()
    {
        if (DryRun) return;
        if (_settings.AudioOutput is { } output && !output.StartsWith('{'))
        {
            _settings.AudioOutput = null; // 1.0 stored a VoiceMeeter device name here, not an endpoint id
            _settings.Save();
        }
        if (_settings.ChannelEndpoints.Count == 0) return;
        UndoVoiceMeeterSetup();
        _settings.AudioRouted = false; // Hub 1.0's "routed" meant VoiceMeeter; the user turns the engine on
        _settings.Save();
    }

    /// <summary>
    /// Undoes Hub's changes to Windows audio (run by the uninstaller): the default devices and the sink's name go
    /// back, and anything Hub 1.0 set up for VoiceMeeter and Equalizer APO is restored.
    /// </summary>
    public List<string> Cleanup()
    {
        var done = new List<string>();
        if (DryRun) return done;
        if (_settings.AudioRouted || _settings.SinkOriginalName is not null)
        {
            RestoreDefaults(_settings);
            done.Add("Windows sound devices restored");
        }
        done.AddRange(UndoVoiceMeeterSetup());
        _settings.AudioRouted = false;
        _settings.Save();
        return done;
    }

    /// <summary>
    /// Equalizer APO's config.txt goes back to the copy saved before Hub 1.0's first write, hidden devices are shown
    /// again and the VoiceMeeter inputs get their names back. VoiceMeeter's own routing is left as it is.
    /// </summary>
    List<string> UndoVoiceMeeterSetup()
    {
        var done = new List<string>();
        if (EapoConfigDir() is { } dir)
        {
            var config = Path.Combine(dir, "config.txt");
            var backup = config + ".before-hub";
            if (File.Exists(backup)) { File.Copy(backup, config, overwrite: true); File.Delete(backup); done.Add("Equalizer APO config restored"); }
            else if (File.Exists(config) && File.ReadAllText(config).Contains("# Managed by Hub")) { File.WriteAllText(config, ""); done.Add("Equalizer APO config cleared"); }
            var hubDir = Path.Combine(dir, "hub");
            foreach (var f in Directory.Exists(hubDir) ? Directory.GetFiles(hubDir) : [])
                try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the old limiter DLL stays until the audio engine restarts */ }
        }
        if (_settings.HiddenEndpoints.Count > 0)
        {
            foreach (var id in _settings.HiddenEndpoints)
                try { AudioEndpoints.SetVisible(id, true); } catch (Exception) { /* device removed */ }
            _settings.HiddenEndpoints.Clear();
            done.Add("hidden sound devices shown");
        }
        var endpoints = AudioEndpoints.List();
        foreach (var (channel, guid) in _settings.ChannelEndpoints)
            if (LegacyEndpointNames.GetValueOrDefault(channel) is { } name
                && endpoints.FirstOrDefault(e => e.IsRender && e.Guid == guid) is { } ep
                && !ep.Name.StartsWith(name + " (", StringComparison.Ordinal))
            {
                try { AudioEndpoints.Rename(ep.Id, name); done.Add($"{ep.Name} renamed back to {name}"); }
                catch (Exception) { /* leave the name */ }
            }
        _settings.ChannelEndpoints.Clear();
        _settings.Save();
        return done;
    }

    /// <summary>The VoiceMeeter inputs Hub 1.0 renamed to Game/Chat/Media.</summary>
    static readonly Dictionary<string, string> LegacyEndpointNames = new()
    {
        ["game"] = "Voicemeeter Input", ["chat"] = "Voicemeeter AUX Input", ["media"] = "Voicemeeter VAIO3 Input",
    };

    static string? EapoConfigDir()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
        var dir = key?.GetValue("ConfigPath") as string
                  ?? (key?.GetValue("InstallPath") is string install ? Path.Combine(install, "config") : null);
        return dir is not null && Directory.Exists(dir) ? dir : null;
    }

    public void Dispose() => Shutdown();
}
