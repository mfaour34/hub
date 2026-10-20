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

        Line(failures == 0 ? "RESULT: all checks passed" : $"RESULT: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>`Hub.exe --audiotest [log]`: the read-only part of Configure routing, step by step.</summary>
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
            Line($"status: voicemeeter={s.VoiceMeeter} eapo={s.Eapo} routed={s.Routed} steps=[{string.Join(" | ", s.Steps)}]");
            foreach (var e in AudioEndpoints.List().Where(e => e.Active && (e.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("PRO X 2"))))
                Line($"  endpoint {(e.IsRender ? "out" : "in ")} {e.Name} {e.Guid} eapo={e.HasEqualizerApo}");
            foreach (var app in audio.Apps()) Line($"  app {app.Name} channel={app.Channel ?? "-"} playing={app.Playing}");
            foreach (var sess in AudioSessions.List().Take(3)) Line($"  override pid {sess.ProcessId} {sess.ProcessName}: {AudioSessions.GetAppOutput(sess.ProcessId) ?? "(follows default)"}");
            foreach (var e in audio.UnusedDevices()) Line($"  unused {e.Name}");
            Line($"dll: {VoiceMeeter.DllPath()}");
            using var vm = new VoiceMeeter();
            Line($"login ok, type={vm.Type} (3 = Potato)");
            foreach (var (type, name) in vm.OutputDevices) Line($"  vm output type={type} {name}");
            Line($"Strip[5].Gain={vm.Get("Strip[5].Gain")} Bus[0].Gain={vm.Get("Bus[0].Gain")}");
            Line("RESULT: ok");
            return 0;
        }
        catch (Exception ex)
        {
            Line($"FAIL: {ex}");
            return 1;
        }
    }

    /// <summary>`Hub.exe --tidy [log]`: runs exactly what the "Rename &amp; hide" button runs, logging any exception in full.</summary>
    public static int RunTidy(string logPath)
    {
        using var log = new StreamWriter(logPath, append: false);
        try
        {
            var settings = AppSettings.Load();
            var audio = new AudioService(settings);
            log.WriteLine("rename…"); log.Flush();
            audio.RenameChannelDevices();
            log.WriteLine("hide…"); log.Flush();
            log.WriteLine($"hidden {audio.HideUnusedDevices()}");
            log.WriteLine("RESULT: ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.WriteLine($"FAIL: {ex}");
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
