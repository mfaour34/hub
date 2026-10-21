using System.Text.Json;
using System.Text.Json.Nodes;
using Hub.App.Native;
using Hub.Core.Audio;
using Hub.Core.Devices;
using Hub.Core.Diagnostics;
using Hub.Core.Logitech;
using Hub.Core.Razer;
using Hub.Core.Ui;
using Microsoft.Win32;

namespace Hub.App;

/// <summary>Request/response bridge between the WebView2 UI (wwwroot/app.js) and the device/audio services.</summary>
public sealed class Bridge(DeviceManager devices, AudioService audio, AppSettings settings, SystemInfo.StabilityWatcher stability, SystemInfo.DeviceWatcher usb)
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool Autostart
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Hub") is not null;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Hub", $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue("Hub", throwOnMissingValue: false);
        }
    }

    public static string DevicesEventJson() => """{"event":"devices"}""";

    public string StateJson() => JsonSerializer.Serialize(new { @event = "state", data = State() }, Json);

    object State() => new
    {
        mouse = new
        {
            connected = devices.Mouse is not null,
            viaDongle = devices.Mouse?.ViaDongle ?? false,
            battery = devices.MouseBattery is { } b ? new { percent = b.Percent, charging = b.Charging } : null,
            summary = devices.MouseSummary,
            model = MouseModelJson(devices.MouseModel ?? DeviceCatalog.Mice[0], devices.MouseCaps),
        },
        headset = new
        {
            dongle = devices.Headset is not null,
            on = devices.Headset?.HeadsetConnected ?? false,
            battery = devices.HeadsetBattery is { } h ? new { percent = h.Percent, charging = h.Charging } : null,
            summary = devices.HeadsetSummary,
            model = HeadsetModelJson(devices.HeadsetModel ?? DeviceCatalog.Headsets[0], devices.HeadsetCaps),
        },
        vendors = DeviceManager.RunningVendorApps(),
        alertPercent = settings.LowBatteryNotifyPercent,
        mouseColor = settings.MouseColor,
        headsetColor = settings.HeadsetColor,
        devicePictures = settings.DevicePictures == "drawing" ? "drawing" : "photo",
        audioSummary = audio.Summary(),
        stability = StabilityJson(),
    };

    /// <summary>Handles one message {id, cmd, args} and returns the JSON reply.</summary>
    public async Task<string> HandleAsync(string message)
    {
        var msg = JsonNode.Parse(message)!;
        int id = msg["id"]!.GetValue<int>();
        string cmd = msg["cmd"]!.GetValue<string>();
        var args = msg["args"] as JsonObject ?? [];
        try
        {
            var data = await Dispatch(cmd, args);
            return JsonSerializer.Serialize(new { id, ok = true, data }, Json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { id, ok = false, error = ex.Message }, Json);
        }
    }

    async Task<object?> Dispatch(string cmd, JsonObject a)
    {
        switch (cmd)
        {
            case "state": return State();

            // ---------------- mouse ----------------
            case "mouse.load": return await OnMouse(LoadMouse);
            case "mouse.setStages":
            {
                int active = a["active"]!.GetValue<int>();
                var stages = a["stages"]!.AsArray().Select(s => new DpiStage(s!.GetValue<int>(), s!.GetValue<int>())).ToList();
                await OnMouse(m => { m.SetDpiStages(new DpiStages(active, stages)); m.SetDpi(stages[active - 1]); return 0; });
                devices.RefreshSummaries();
                return null;
            }
            case "mouse.setPolling":
                await OnMouse(m => { m.SetPollingRate(a["hz"]!.GetValue<int>()); return 0; });
                devices.RefreshSummaries();
                return null;
            case "mouse.setPower":
                return await OnMouse(m =>
                {
                    m.SetIdleTimeout(TimeSpan.FromMinutes(a["idleMinutes"]!.GetValue<int>()));
                    m.SetLowBatteryThresholdPercent(a["lowBattery"]!.GetValue<int>());
                    return 0;
                });
            case "mouse.setLift":
                return await OnMouse(m =>
                {
                    if (a["asymmetric"]!.GetValue<bool>())
                        m.SetAsymmetricLiftOff(new LiftOff(a["liftOff"]!.GetValue<int>(), a["landing"]!.GetValue<int>()));
                    else m.SetSymmetricLiftOff(a["tracking"]!.GetValue<int>());
                    return 0;
                });
            case "mouse.setDyn":
            {
                bool enabled = a["enabled"]!.GetValue<bool>();
                var mode = (DynamicSensitivityMode)a["mode"]!.GetValue<int>();
                var points = a["points"] is JsonArray arr
                    ? arr.Select(p => new CurvePoint(p!["x"]!.GetValue<double>(), p!["y"]!.GetValue<double>())).ToList()
                    : null;
                await OnMouse(m =>
                {
                    m.SetDynamicSensitivityEnabled(enabled);
                    if (!enabled) return 0;
                    if (mode == DynamicSensitivityMode.Custom && points is not null) m.SetCustomCurve(points);
                    else m.SetDynamicSensitivityMode(mode);
                    return 0;
                });
                if (points is not null)
                {
                    settings.CustomCurve = points.Select(p => new CurvePointDto(p.X, p.Y)).ToList();
                    settings.Save();
                }
                return null;
            }
            case "mouse.setRotation":
                return await OnMouse(m => { m.SetRotation(a["enabled"]!.GetValue<bool>(), a["angle"]!.GetValue<int>()); return 0; });
            case "mouse.setButton":
            {
                byte id = (byte)a["id"]!.GetValue<int>();
                if (id == ViperButtons.Left) throw new InvalidOperationException("Left click can't be remapped");
                if (devices.MouseCaps?.Buttons.Contains(id) != true) throw new InvalidOperationException($"Button {id} isn't on this mouse");
                var action = a["action"] as JsonObject;
                var assignment = action is null
                    ? ButtonAssignment.Default(id)
                    : new ButtonAssignment(id, (ButtonFunction)action["fn"]!.GetValue<int>(),
                        action["data"]!.AsArray().Select(v => (byte)v!.GetValue<int>()).ToArray());
                return await OnMouse(m => { m.SetButton(assignment); return Button(m.GetButton(id)); });
            }

            // ---------------- headset ----------------
            case "headset.load": return await LoadHeadset();
            case "headset.setEq":
            {
                if (devices.HeadsetCaps?.Eq != true) throw new InvalidOperationException("This headset's EQ layout isn't supported");
                var gains = a["gains"]!.AsArray().Select(g => g!.GetValue<int>()).ToArray();
                await Headset().SetEqAsync(gains, a["save"]!.GetValue<bool>());
                devices.RefreshSummaries();
                return null;
            }
            case "headset.setSidetone": await Headset().SetSidetoneAsync(a["level"]!.GetValue<int>()); return null;
            case "headset.setNr": await Headset().SetNoiseReductionAsync(a["enabled"]!.GetValue<bool>()); return null;
            case "headset.setSleep": await Headset().SetSleepMinutesAsync(a["minutes"]!.GetValue<int>()); return null;
            case "headset.savePreset":
            {
                var name = a["name"]!.GetValue<string>().Trim();
                var gains = a["gains"]!.AsArray().Select(g => g!.GetValue<int>()).ToArray();
                var existing = settings.HeadsetEqPresets.FirstOrDefault(p => p.Name == name);
                if (existing is null) settings.HeadsetEqPresets.Add(existing = new EqPreset { Name = name });
                existing.Gains = gains;
                settings.Save();
                return settings.HeadsetEqPresets.Select(p => new { name = p.Name, gains = p.Gains });
            }

            // ---------------- audio ----------------
            case "audio.load": return await Sta.Run(LoadAudio);
            case "audio.setPreset":
            {
                var ch = Channel(a);
                return await Sta.Run(() => { audio.SetPreset(ch.Channel, a["name"]!.GetValue<string>()); return ChannelJson(ch); });
            }
            case "audio.saveCustom":
            {
                var ch = Channel(a);
                var p = a["preset"]!;
                var preset = new CustomAudioPreset
                {
                    Name = p["name"]!.GetValue<string>(),
                    GainDb = p["gain"]!.GetValue<double>(),
                    Limiter = p["limiter"]!.GetValue<bool>(),
                    AutoHeadroom = p["autoHeadroom"]!.GetValue<bool>(),
                    SampleRate = p["rate"]!.GetValue<int>(),
                    Filters = p["filters"]!.AsArray().Select(f => new EqFilter(
                        Enum.Parse<FilterType>(f!["type"]!.GetValue<string>()), f["freq"]!.GetValue<double>(),
                        f["gain"]!.GetValue<double>(), f["q"]!.GetValue<double>(), f["enabled"]!.GetValue<bool>())).ToList(),
                };
                var original = a["original"]?.GetValue<string>();
                return await Sta.Run(() => { audio.SaveCustom(ch.Channel, original, preset); return ChannelJson(ch); });
            }
            case "audio.deleteCustom":
            {
                var ch = Channel(a);
                return await Sta.Run(() => { audio.DeleteCustom(ch.Channel, a["name"]!.GetValue<string>()); return ChannelJson(ch); });
            }
            case "audio.setVolume": await Sta.Run(() => audio.SetVolume(Channel(a).Channel, a["value"]!.GetValue<double>())); return null;
            case "audio.setTame":
                return await Sta.Run(() => { var ch = Channel(a); audio.SetTame(ch.Channel, a["value"]!.GetValue<double>()); return ChannelJson(ch); });
            case "audio.setChatMix": await Sta.Run(() => audio.SetChatMix(a["value"]!.GetValue<double>())); return null;
            case "audio.start": await Sta.Run(audio.Start); return null;
            case "audio.stop": await Sta.Run(audio.Stop); return null;
            case "audio.apps":
                return await Sta.Run(() => audio.Apps().Select(x => new { name = x.Name, channel = x.Channel, playing = x.Playing }));
            case "audio.assignApp":
                await Sta.Run(() => audio.AssignApp(a["name"]!.GetValue<string>(), a["channel"]!.GetValue<string>()));
                return null;
            case "audio.io":
                return await Sta.Run(() => new
                {
                    outputs = audio.OutputChoices().Select(e => new { id = e.Id, name = e.Name }),
                    output = audio.CurrentOutput(),
                    outputChosen = settings.AudioOutput is not null,
                    mics = audio.MicChoices().Select(e => new { id = e.Id, name = e.Name }),
                    mic = audio.CurrentMic(),
                    micChosen = settings.MicEndpoint is not null,
                });
            case "audio.setOutput": await Sta.Run(() => audio.SetOutput(a["id"]?.GetValue<string>())); return null;
            case "audio.setMic": await Sta.Run(() => audio.SetMic(a["id"]?.GetValue<string>())); return null;

            // ---------------- system ----------------
            case "system.specs":
                return await Task.Run(async () =>
                {
                    var sections = SystemInfo.Specs.Read(await MouseInfo(), await HeadsetInfo());
                    return new
                    {
                        sections = sections.Where(s => s.Rows.Count > 0).Select(s => new { title = s.Title, rows = s.Rows.Select(r => new { label = r.Label, value = r.Value }) }),
                        text = SpecsText.Format(sections),
                    };
                });
            case "system.live":
                return await Task.Run(() =>
                {
                    var r = SystemInfo.Live.Read();
                    return new { cpuLoad = r.CpuLoad, cpuMhz = r.CpuMhz, gpuTempC = r.GpuTempC, gpuLoad = r.GpuLoad };
                });
            case "system.events":
                return stability.Log.Recent(DateTimeOffset.Now).Select(e => new
                {
                    kind = e.Kind, title = e.Title, detail = e.Detail, time = e.Time.ToString("o"), log = e.Log, recordId = e.RecordId,
                });
            case "system.devices":
                return await Task.Run(() => new
                {
                    connected = usb.Connected().Select(d => new { name = d.Name, kind = d.Kind }),
                    activity = usb.Log.Recent(DateTimeOffset.Now).Select(e => new { time = e.Time.ToString("o"), action = e.Action, name = e.Name, kind = e.Kind }),
                });
            case "system.openEvent":
                OpenEventViewer(a["log"]!.GetValue<string>(), a["recordId"]!.GetValue<long>());
                return null;

            // ---------------- settings ----------------
            case "settings.load":
                return new
                {
                    version = Program.Version, autostart = Autostart, alertPercent = settings.LowBatteryNotifyPercent, restoreNormalMode = settings.RestoreMouseNormalMode,
                    mouseColor = settings.MouseColor, headsetColor = settings.HeadsetColor,
                    theme = ThemeTokens.IsPreset(settings.Theme) ? settings.Theme : ThemeTokens.DefaultPreset,
                    customTheme = ThemeTokens.Sanitize(settings.CustomTheme),
                    followWindows = settings.FollowWindowsTheme,
                    devicePictures = settings.DevicePictures == "drawing" ? "drawing" : "photo",
                };
            case "settings.save":
                Autostart = a["autostart"]!.GetValue<bool>();
                settings.LowBatteryNotifyPercent = a["alertPercent"]!.GetValue<int>();
                settings.RestoreMouseNormalMode = a["restoreNormalMode"]!.GetValue<bool>();
                settings.MouseColor = a["mouseColor"]?.GetValue<string>() is "white" ? "white" : "black";
                settings.HeadsetColor = a["headsetColor"]?.GetValue<string>() is "white" ? "white" : "black";
                SaveAppearance(a);
                settings.Save();
                return null;
        }
        throw new InvalidOperationException($"Unknown command {cmd}");
    }

    // ---------------- helpers ----------------

    /// <summary>7-day counts for Overview and the System page heading.</summary>
    object StabilityJson()
    {
        var recent = stability.Log.Recent(DateTimeOffset.Now);
        int Count(string kind) => recent.Count(e => e.Kind == kind);
        return new { gpuResets = Count("gpu-reset"), hardwareErrors = Count("hardware-error"), bluescreens = Count("bluescreen"), restarts = Count("unexpected-restart") };
    }

    async Task<SystemInfo.DeviceInfo> MouseInfo()
    {
        if (devices.Mouse is not { } m) return new(null, null);
        string? fw = null;
        try { fw = await Task.Run(m.GetFirmwareVersion).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        return new(devices.MouseModel?.FullName, fw);
    }

    async Task<SystemInfo.DeviceInfo> HeadsetInfo()
    {
        if (devices.Headset is not { HeadsetConnected: true } h) return new(devices.Headset is null ? null : devices.HeadsetModel?.FullName, null);
        string? fw = null;
        try { fw = await h.GetFirmwareAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        return new(devices.HeadsetModel?.FullName, fw);
    }

    /// <summary>Event Viewer filtered to one record (System or Application only: they are the logs Hub reads).</summary>
    static void OpenEventViewer(string log, long recordId)
    {
        if (log is not ("System" or "Application")) throw new ArgumentException("Unknown log");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("eventvwr.exe",
            $"/c:{log} /f:\"*[System[EventRecordID={recordId}]]\"") { UseShellExecute = true });
    }

    /// <summary>Theme fields of settings.save; each is optional and an invalid value falls back to the default.</summary>
    void SaveAppearance(JsonObject a)
    {
        if (a["theme"]?.GetValue<string>() is { } theme)
            settings.Theme = ThemeTokens.IsPreset(theme) ? theme : ThemeTokens.DefaultPreset;
        if (a["customTheme"] is JsonObject custom)
            settings.CustomTheme = ThemeTokens.Sanitize(custom.Where(p => p.Value is JsonValue)
                .ToDictionary(p => p.Key, p => p.Value!.ToString()));
        if (a["followWindows"]?.GetValue<bool>() is { } follow) settings.FollowWindowsTheme = follow;
        if (a["devicePictures"]?.GetValue<string>() is { } pictures) settings.DevicePictures = pictures == "drawing" ? "drawing" : "photo";
        if (a["windowBackground"]?.GetValue<string>() is { } bg) settings.WindowBackground = ThemeTokens.IsColor(bg) ? bg.ToLowerInvariant() : "#dedbd3";
        AppearanceChanged?.Invoke();
    }

    /// <summary>Raised after the page saves appearance settings (theme, pictures), so the window can follow.</summary>
    public event Action? AppearanceChanged;

    /// <summary>The window colour before the page paints (the theme's background).</summary>
    public Color WindowBackground => ColorTranslator.FromHtml(
        ThemeTokens.BackgroundFor(settings.Theme, settings.FollowWindowsTheme, WindowsUsesDarkApps(), settings.WindowBackground));

    static bool WindowsUsesDarkApps() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    /// <summary>The page's appearance settings as JSON, injected before the page's first paint so it starts in its theme.</summary>
    public string AppearanceJson() => JsonSerializer.Serialize(new
    {
        theme = ThemeTokens.IsPreset(settings.Theme) ? settings.Theme : ThemeTokens.DefaultPreset,
        customTheme = ThemeTokens.Sanitize(settings.CustomTheme),
        followWindows = settings.FollowWindowsTheme,
        devicePictures = settings.DevicePictures == "drawing" ? "drawing" : "photo",
    }, Json);

    /// <summary>
    /// The model the UI draws. While nothing is connected it shows the first catalogued model;
    /// caps are null until the device has been probed, which the UI treats as "show everything".
    /// </summary>
    static object MouseModelJson(MouseModel m, MouseCapabilities? c) => new
    {
        name = m.Name, fullName = m.FullName, known = m.Known, render = m.Render, colors = m.Colors, wireless = m.WirelessName,
        buttons = m.Buttons.Where(b => c is null || c.Buttons.Contains(b.Id)).Select(b => new { id = b.Id, name = b.Name, left = b.Left, top = b.Top }),
        caps = c is null ? null : new
        {
            dpiStages = c.DpiStages, polling = c.Polling, battery = c.Battery, powerSaving = c.PowerSaving,
            liftOff = c.LiftOff, dynamicSensitivity = c.DynamicSensitivity, rotation = c.Rotation,
        },
    };

    static object HeadsetModelJson(HeadsetModel m, HeadsetCapabilities? c) => new
    {
        name = m.Name, fullName = m.FullName, known = m.Known, render = m.Render, colors = m.Colors, wireless = m.WirelessName,
        caps = c is null ? null : new { battery = c.Battery, sleep = c.Sleep, sidetone = c.Sidetone, noiseReduction = c.NoiseReduction, eq = c.Eq },
    };

    Task<T> OnMouse<T>(Func<RazerMouse, T> f)
    {
        var m = devices.Mouse ?? throw new InvalidOperationException("Mouse not connected");
        return Task.Run(() => f(m));
    }

    CenturionHeadset Headset() =>
        devices.Headset is { HeadsetConnected: true } h ? h : throw new InvalidOperationException("Headset is off");

    object LoadMouse(RazerMouse m)
    {
        var c = devices.MouseCaps ?? throw new InvalidOperationException("Mouse is still being identified");
        var model = devices.MouseModel!;
        var stages = c.DpiStages ? m.GetDpiStages() : null;
        var lift = c.LiftOff ? (Mode: m.GetLiftMode(), Distances: m.GetLiftOff()) : default;
        var rotation = c.Rotation ? m.GetRotation() : default;
        return new
        {
            viaDongle = m.ViaDongle,
            firmware = m.GetFirmwareVersion(),
            pollingRates = m.SupportedPollingRates,
            polling = c.Polling ? m.GetPollingRate() : 0,
            stages = stages?.Stages.Select(s => s.X) ?? [],
            activeStage = stages?.ActiveStage ?? 1,
            idleMinutes = c.PowerSaving ? Math.Clamp((int)m.GetIdleTimeout().TotalMinutes, 1, 15) : 0,
            lowBattery = c.PowerSaving ? Math.Clamp((int)Math.Round(m.GetLowBatteryThresholdPercent() / 5.0) * 5, 5, 100) : 0,
            lift = c.LiftOff
                ? new { asymmetric = lift.Mode.Asymmetric, tracking = Math.Clamp(lift.Mode.TrackingDistance, 1, 3), liftOff = lift.Distances.LiftOffDistance, landing = lift.Distances.LandingDistance }
                : null,
            dyn = c.DynamicSensitivity
                ? new
                {
                    enabled = m.GetDynamicSensitivityEnabled(),
                    mode = (int)m.GetDynamicSensitivityMode(),
                    customPoints = settings.CustomCurve.Select(p => new { x = p.X, y = p.Y }),
                }
                : null,
            rotation = c.Rotation ? new { enabled = rotation.Enabled, angle = rotation.Angle } : null,
            buttons = c.Buttons.ToDictionary(b => b.ToString(), b => Button(m.GetButton(b))),
        };
    }

    static object Button(ButtonAssignment b)
    {
        var d = ButtonAssignment.Default(b.ButtonId);
        return new { fn = (int)b.Function, data = b.Data.Select(x => (int)x), isDefault = d.Function == b.Function && d.Data.SequenceEqual(b.Data) };
    }

    async Task<object> LoadHeadset()
    {
        var h = Headset();
        var c = devices.HeadsetCaps ?? throw new InvalidOperationException("Headset is still being identified");
        return new
        {
            firmware = await h.GetFirmwareAsync(),
            eq = c.Eq ? await h.GetEqAsync() : null,
            sidetone = c.Sidetone ? await h.GetSidetoneAsync() : 0,
            noiseReduction = c.NoiseReduction && await h.GetNoiseReductionAsync(),
            sleep = c.Sleep ? await h.GetSleepMinutesAsync() : 0,
            presets = settings.HeadsetEqPresets.Select(p => new { name = p.Name, gains = p.Gains }),
        };
    }

    object LoadAudio()
    {
        var s = audio.GetStatus();
        return new
        {
            status = new { running = s.Running, on = settings.AudioRouted, output = s.Output, sink = s.Sink, blocker = s.Blocker, problem = s.Problem },
            chatMix = settings.ChatMix,
            channels = AudioService.Channels.Select(ChannelJson),
        };
    }

    object ChannelJson(AudioService.ChannelDef c)
    {
        var active = audio.ActivePreset(c.Channel);
        var chain = audio.ActiveChain(c.Channel);
        var d = AudioService.DefaultChain(c.Channel);
        return new
        {
            id = c.Id, name = c.Name,
            presets = audio.Presets(c.Channel).Select(p => new { name = p.Name, category = p.Category })
                .Concat(audio.CustomPresets(c.Channel).Select(p => new { name = p.Name, category = "Custom" })),
            defaultChoice = audio.DefaultChoice(c.Channel),
            volume = audio.Volume(c.Channel),
            tame = audio.Tame(c.Channel),
            maxTame = AudioService.MaxTameDb,
            active = active.Name,
            custom = audio.IsCustomActive(c.Channel),
            gain = active.GainDb,
            preamp = chain.Limiter || !chain.AutoHeadroom ? active.GainDb : active.GainDb - active.PeakBoostDb(chain.SampleRate),
            limiter = chain.Limiter,
            autoHeadroom = chain.AutoHeadroom,
            rate = chain.SampleRate,
            rates = AudioService.SampleRates,
            defaults = new { limiter = d.Limiter, autoHeadroom = d.AutoHeadroom, rate = d.SampleRate },
            filters = active.Filters.Select(f => new { type = f.Type.ToString(), freq = f.Frequency, gain = f.GainDb, q = f.Q, enabled = f.Enabled }),
        };
    }

    static AudioService.ChannelDef Channel(JsonObject a) =>
        AudioService.Channels.First(c => c.Id == a["channel"]!.GetValue<string>());
}
