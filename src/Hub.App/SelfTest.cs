using Hub.App.Native;
using Hub.Core.Logitech;
using Hub.Core.Razer;

namespace Hub.App;

/// <summary>
/// `Hub.exe --selftest [log path]`: discovers both devices through the real native layer and runs
/// read-only queries only. Writes a report and exits.
/// </summary>
static class SelfTest
{
    public static int Run(string logPath)
    {
        using var log = new StreamWriter(logPath, append: false);
        void Line(string s) { log.WriteLine(s); log.Flush(); }
        int failures = 0;
        void Check(string name, Func<object?> read)
        {
            try { Line($"OK   {name}: {Format(read())}"); }
            catch (Exception ex) { failures++; Line($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Line($"Hub self-test {DateTime.Now:s}; vendor apps running: {string.Join(", ", DeviceManager.RunningVendorApps())}");

        var mice = HidApi.Enumerate(RazerMouse.VendorId)
            .Where(d => d.ProductId is RazerMouse.WiredProductId or RazerMouse.DongleProductId && d.UsagePage == 1 && d.Usage == 2 && d.FeatureLength == RazerPacket.ReportLength)
            .ToList();
        Line($"Razer candidates: {mice.Count}");
        foreach (var info in mice)
        {
            Line($"  {info.Path} pid={info.ProductId:X4}");
            using var mouse = new RazerMouse(new WinFeatureChannel(info.Path), info.ProductId == RazerMouse.DongleProductId);
            Check("firmware", mouse.GetFirmwareVersion);
            Check("mode", () => mouse.GetDeviceMode());
            Check("polling", () => mouse.GetPollingRate());
            Check("dpi", mouse.GetDpi);
            Check("stages", () => mouse.GetDpiStages());
            Check("battery", mouse.GetBattery);
            Check("idle", () => mouse.GetIdleTimeout());
            Check("low battery %", () => mouse.GetLowBatteryThresholdPercent());
            Check("lift-off", mouse.GetLiftOff);
            Check("dynamic sensitivity", () => (mouse.GetDynamicSensitivityEnabled(), mouse.GetDynamicSensitivityMode()));
            Check("bottom button", () => mouse.GetButton(ViperButtons.BottomDpi).Describe());
            Check("back button", () => mouse.GetButton(ViperButtons.Back).Describe());
            Check("capabilities", () => mouse.Probe(Hub.Core.Devices.DeviceCatalog.Mice[0].Buttons.Select(b => b.Id)) is var c
                ? $"stages={c.DpiStages} polling={c.Polling} battery={c.Battery} power={c.PowerSaving} lift={c.LiftOff} dyn={c.DynamicSensitivity} rotation={c.Rotation} buttons=[{string.Join(",", c.Buttons)}]"
                : null);
        }

        var headsetInfo = HidApi.Enumerate(CenturionHeadset.VendorId)
            .FirstOrDefault(d => d.ProductId == CenturionHeadset.ProductId && d.UsagePage == CenturionHeadset.ControlUsagePage);
        Line($"Headset control collection: {headsetInfo?.Path ?? "not found"}");
        if (headsetInfo is not null)
        {
            using var headset = new CenturionHeadset(new WinReportChannel(headsetInfo));
            var events = new List<string>();
            headset.BatteryChanged += b => events.Add($"battery event {b}");
            Check("init", () => { headset.InitializeAsync().GetAwaiter().GetResult(); return headset.HeadsetConnected; });
            if (headset.HeadsetConnected)
            {
                Check("firmware", () => headset.GetFirmwareAsync().GetAwaiter().GetResult());
                Check("battery", () => headset.GetBatteryAsync().GetAwaiter().GetResult());
                Check("sleep minutes", () => headset.GetSleepMinutesAsync().GetAwaiter().GetResult());
                Check("sidetone", () => headset.GetSidetoneAsync().GetAwaiter().GetResult());
                Check("noise reduction", () => headset.GetNoiseReductionAsync().GetAwaiter().GetResult());
                Check("eq live", () => headset.GetEqAsync().GetAwaiter().GetResult());
                Check("eq stored", () => headset.GetEqAsync(stored: true).GetAwaiter().GetResult());
                Check("eq bands", () => headset.GetEqFrequenciesAsync().GetAwaiter().GetResult());
                Check("features", () => string.Join(" ", new[] { CenturionFeature.BatterySoc, CenturionFeature.AutoSleep, CenturionFeature.Sidetone, CenturionFeature.MicSnr, CenturionFeature.OnboardEq }
                    .Select(f => $"{f:X4}={headset.HasFeatureAsync(f).GetAwaiter().GetResult()}")));
            }
            Line($"events seen: {(events.Count == 0 ? "none" : string.Join("; ", events))}");
        }

        Check("specs", () => SystemInfo.Specs.Read(new(null, null), new(null, null)).Sum(s => s.Rows.Count) + " rows");
        Check("live", () => SystemInfo.Live.Read());

        Line(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>`Hub.exe --mousetest seconds [log]`: a raw-input test session, logging its numbers once a second.</summary>
    public static int RunMouse(int seconds, string logPath)
    {
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        using var mouse = new RawMouse();
        mouse.Start();
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var s = mouse.Snapshot();
            log.WriteLine($"{i + 1}s device={s.Device ?? "-"} hz={s.Polling.Hz:0} steady={s.Polling.Steady:P0} samples={s.Polling.Samples} " +
                $"history=[{string.Join(" ", s.Polling.History.Select(h => h.ToString("0")))}] buttons=[{string.Join(" ", s.Buttons.Select(b => $"{b.Button}:{b.Presses}/{b.Chatter}"))}]");
        }
        log.WriteLine("RESULT: ok");
        return 0;
    }

    /// <summary>`Hub.exe --systemtest [log]`: the System page's specs, live readings and the crash-log catch-up, read-only.</summary>
    public static int RunSystem(string logPath)
    {
        using var log = new StreamWriter(logPath, append: false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sections = SystemInfo.Specs.Read(new(null, null), new(null, null));
        log.WriteLine(Hub.Core.Diagnostics.SpecsText.Format(sections));
        log.WriteLine($"specs read in {watch.ElapsedMilliseconds} ms ({string.Join(", ", sections.Select(s => s.Title))})");
        watch.Restart();
        log.WriteLine($"live: {SystemInfo.Live.Read()} in {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var usb = new SystemInfo.DeviceWatcher(Path.Combine(Path.GetTempPath(), "hub-devices-test.jsonl"), _ => null).Connected();
        log.WriteLine($"usb devices: {usb.Count} in {watch.ElapsedMilliseconds} ms");
        foreach (var d in usb) log.WriteLine($"  {d.Kind,-10} {d.Name}  [{d.Id}]");
        // crash-log catch-up into a scratch file (the real log is the running Hub's)
        var path = Path.Combine(Path.GetTempPath(), $"hub-stability-test-{Environment.ProcessId}.jsonl");
        using (var stability = new SystemInfo.StabilityWatcher(path))
        {
            watch.Restart();
            stability.CatchUpNow();
            var events = stability.Log.Recent(DateTimeOffset.Now);
            log.WriteLine($"stability catch-up: {events.Count} entries in {watch.ElapsedMilliseconds} ms");
            foreach (var e in events) log.WriteLine($"  {e.Time:yyyy-MM-dd HH:mm} {e.Kind,-18} {e.Title} — {e.Detail} [{e.Key}]");
        }
        File.Delete(path);
        log.WriteLine("RESULT: ok");
        return 0;
    }

    /// <summary>`Hub.exe --audiotest [log]`: read-only state of Hub audio (no defaults or devices are changed).</summary>
    public static int RunAudio(string logPath)
    {
        using var log = new StreamWriter(logPath, append: false);
        void Line(string s) { log.WriteLine(s); log.Flush(); }
        try
        {
            Line($"Hub audio test {DateTime.Now:s}");
            var settings = AppSettings.Load();
            var audio = new AudioService(settings);
            var s = audio.GetStatus();
            Line($"status: on={settings.AudioRouted} running={s.Running} output={s.Output} sink={s.Sink} blocker={s.Blocker ?? "-"}");
            foreach (var c in AudioService.Channels)
                Line($"  {c.Name}: {audio.ActivePreset(c.Channel).Name} limiter={audio.ActiveChain(c.Channel).Limiter} volume={audio.Volume(c.Channel)}");
            foreach (var e in audio.OutputChoices()) Line($"  output choice {e.Name}");
            foreach (var app in audio.Apps()) Line($"  app {app.Name} channel={app.Channel ?? "-"} playing={app.Playing}");
            Line("RESULT: ok");
            return 0;
        }
        catch (Exception ex)
        {
            Line($"FAIL: {ex}");
            return 1;
        }
    }

    static string Format(object? o) => o switch
    {
        int[] a => $"[{string.Join(", ", a)}]",
        System.Collections.IEnumerable e and not string => string.Join(", ", e.Cast<object>()),
        _ => o?.ToString() ?? "null",
    };
}
