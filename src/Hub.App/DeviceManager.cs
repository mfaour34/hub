using System.Diagnostics;
using Hub.App.Native;
using Hub.Core.Devices;
using Hub.Core.Logitech;
using Hub.Core.Razer;

namespace Hub.App;

/// <summary>
/// Owns the device connections. Rescans every few seconds while a device is missing, polls the mouse battery,
/// and relays headset events. Events are raised on the UI thread via the captured SynchronizationContext.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    readonly SynchronizationContext _ui;
    readonly AppSettings _settings;
    readonly System.Threading.Timer _timer;
    int _tick;
    bool _scanning;

    public RazerMouse? Mouse { get; private set; }
    public CenturionHeadset? Headset { get; private set; }

    /// <summary>The connected models (catalog entry or a generic fallback) and what they support.</summary>
    public MouseModel? MouseModel { get; private set; }
    public MouseCapabilities? MouseCaps { get; private set; }
    public HeadsetModel? HeadsetModel { get; private set; }
    public HeadsetCapabilities? HeadsetCaps { get; private set; }

    public BatteryState? MouseBattery { get; private set; }
    public HeadsetBattery? HeadsetBattery { get; private set; }

    /// <summary>Short chips for the overview, e.g. "800 DPI", "2000 Hz" / "EQ: Counter Strike 2".</summary>
    public IReadOnlyList<string> MouseSummary { get; private set; } = [];
    public IReadOnlyList<string> HeadsetSummary { get; private set; } = [];

    public void RefreshSummaries() => _ = Task.Run(async () =>
    {
        try
        {
            if (Mouse is { } m && MouseCaps is { } caps)
            {
                var chips = new List<string>();
                if (caps.DpiStages) chips.Add($"{m.GetDpi().X} DPI");
                if (caps.Polling) chips.Add($"{m.GetPollingRate()} Hz");
                if (caps.DynamicSensitivity) chips.Add(m.GetDynamicSensitivityEnabled() ? "Dynamic sensitivity" : "No acceleration");
                MouseSummary = chips;
            }
            else MouseSummary = [];
        }
        catch (Exception) { }
        try
        {
            if (Headset is { HeadsetConnected: true } h && HeadsetCaps is { } caps)
            {
                var chips = new List<string>();
                if (caps.Eq)
                {
                    var gains = await h.GetEqAsync();
                    chips.Add("EQ: " + (_settings.HeadsetEqPresets.FirstOrDefault(p => p.Gains.SequenceEqual(gains))?.Name ?? "Custom"));
                }
                if (caps.Sidetone) chips.Add($"Sidetone {await h.GetSidetoneAsync()}");
                if (caps.NoiseReduction) chips.Add((await h.GetNoiseReductionAsync()) ? "Noise reduction on" : "Noise reduction off");
                HeadsetSummary = chips;
            }
            else HeadsetSummary = [];
        }
        catch (Exception) { }
        Raise(() => Changed?.Invoke());
    });

    /// <summary>Something about either device changed (connection, battery, a setting reported by the device).</summary>
    public event Action? Changed;
    public event Action<string>? Notice;

    public DeviceManager(AppSettings settings)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _settings = settings;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Starts discovery and polling; call once the UI that handles <see cref="Changed"/> exists.</summary>
    public void Start() => _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));

    /// <summary>Vendor processes that would fight over the devices.</summary>
    public static IReadOnlyList<string> RunningVendorApps()
    {
        var found = new List<string>();
        if (Process.GetProcessesByName("lghub_agent").Length > 0) found.Add("Logitech G HUB");
        if (Process.GetProcessesByName("RazerAppEngine").Length > 0) found.Add("Razer Synapse");
        if (Process.GetProcessesByName("SteelSeriesGG").Length > 0) found.Add("SteelSeries GG");
        return found;
    }

    async Task TickAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            if (Mouse is null) ConnectMouse();
            if (Headset is null) await ConnectHeadsetAsync();
            else if (Headset.HeadsetConnected && HeadsetCaps is null) { await ProbeHeadsetAsync(); RefreshSummaries(); }
            // Battery: the mouse every minute (12 × 5 s); the headset pushes events about once a minute.
            if (Mouse is not null && _tick % 12 == 0) PollMouseBattery();
            if (Headset is not null && _tick % 60 == 0) await PollHeadsetBatteryAsync();
            _tick++;
        }
        finally { _scanning = false; }
    }

    /// <summary>
    /// Any Razer mouse control collection. Catalogued models (the Viper V3 Pro) open with their verified protocol
    /// details; other Razer mice are identified by read-only probes and only get the features they answer.
    /// </summary>
    void ConnectMouse()
    {
        var candidates = HidApi.Enumerate(RazerMouse.VendorId)
            .Where(d => d.UsagePage == 0x01 && d.Usage == 0x02 && d.FeatureLength == RazerPacket.ReportLength)
            .OrderBy(d => DeviceCatalog.KnownMouse(d.ProductId) is null ? 1 : 0)
            .ThenBy(d => DeviceCatalog.KnownMouse(d.ProductId)?.WiredPids.Contains(d.ProductId) == true ? 0 : 1); // prefer the cable
        foreach (var info in candidates)
        {
            RazerMouse? mouse = null;
            try
            {
                var model = DeviceCatalog.KnownMouse(info.ProductId);
                if (model is not null)
                {
                    bool dongle = model.DonglePids.Contains(info.ProductId);
                    mouse = new RazerMouse(new WinFeatureChannel(info.Path), dongle, model.TransactionId ?? RazerPacket.DefaultTransactionId,
                        dongle ? model.DonglePolling : model.WiredPolling);
                    if (mouse.ViaDongle && !mouse.IsWirelessConnected()) { mouse.Dispose(); continue; }
                }
                else
                {
                    model = DeviceCatalog.GenericMouse(info.ProductId);
                    var channel = new WinFeatureChannel(info.Path);
                    // Unknown models use the slower wireless timing: it works for both cable and dongle.
                    var id = RazerMouse.Identify(channel, viaDongle: true);
                    if (id is null) { channel.Dispose(); continue; }
                    mouse = new RazerMouse(channel, viaDongle: true, id.Value.TransactionId, id.Value.Polling);
                    try { if (!mouse.IsWirelessConnected()) { mouse.Dispose(); continue; } }
                    catch (RazerException ex) when (ex.Status == RazerStatus.NotSupported) { } // wired-only mouse
                }
                if (_settings.RestoreMouseNormalMode && !AudioService.DryRun && !RunningVendorApps().Contains("Razer Synapse")
                    && mouse.GetDeviceMode() != DeviceMode.Normal)
                {
                    mouse.SetDeviceMode(DeviceMode.Normal);
                    Raise(() => Notice?.Invoke("Mouse switched from driver mode back to normal mode, so its onboard settings apply."));
                }
                MouseCaps = mouse.Probe(model.Buttons.Select(b => b.Id));
                MouseModel = model;
                Mouse = mouse;
                PollMouseBattery();
                RefreshSummaries();
                Raise(() => Changed?.Invoke());
                return;
            }
            catch (Exception)
            {
                mouse?.Dispose();
            }
        }
    }

    void PollMouseBattery()
    {
        try
        {
            if (MouseCaps?.Battery != true) { Mouse!.GetFirmwareVersion(); return; } // wired-only: just check it's still there
            MouseBattery = Mouse!.GetBattery();
            CheckLow("Mouse", MouseBattery.Percent, MouseBattery.Charging);
        }
        catch (RazerException ex) when (ex.Status == RazerStatus.Timeout)
        {
            MouseBattery = null; // asleep; keep the connection
        }
        catch (Exception)
        {
            DropMouse();
            return;
        }
        Raise(() => Changed?.Invoke());
    }

    public void DropMouse()
    {
        Mouse?.Dispose();
        Mouse = null;
        MouseModel = null;
        MouseCaps = null;
        MouseBattery = null;
        Raise(() => Changed?.Invoke());
    }

    /// <summary>Any Logitech Centurion control collection (usage page 0xFFA0); the PRO X 2 is preferred.</summary>
    async Task ConnectHeadsetAsync()
    {
        foreach (var info in HidApi.Enumerate(CenturionHeadset.VendorId)
                     .Where(d => d.UsagePage == CenturionHeadset.ControlUsagePage)
                     .OrderBy(d => DeviceCatalog.KnownHeadset(d.ProductId) is null ? 1 : 0))
            if (await TryConnectHeadsetAsync(info)) return;
    }

    async Task<bool> TryConnectHeadsetAsync(HidDeviceInfo info)
    {
        CenturionHeadset? headset = null;
        try
        {
            headset = new CenturionHeadset(new WinReportChannel(info));
            await headset.InitializeAsync();
            HeadsetModel = DeviceCatalog.KnownHeadset(info.ProductId) ?? DeviceCatalog.GenericHeadset(info.ProductId);
            headset.BatteryChanged += b => { HeadsetBattery = b; CheckLow("Headset", b.Percent, b.Charging); Raise(() => Changed?.Invoke()); };
            headset.SidetoneChanged += _ => Raise(() => Changed?.Invoke());
            headset.NoiseReductionChanged += _ => Raise(() => Changed?.Invoke());
            headset.ConnectionChanged += on =>
            {
                if (on) _ = Task.Run(async () => { await ProbeHeadsetAsync(); await PollHeadsetBatteryAsync(); RefreshSummaries(); });
                else HeadsetBattery = null;
                Raise(() => Changed?.Invoke());
            };
            headset.Disconnected += DropHeadset;
            Headset = headset;
            await ProbeHeadsetAsync();
            await PollHeadsetBatteryAsync();
            RefreshSummaries();
            Raise(() => Changed?.Invoke());
            return true;
        }
        catch (Exception)
        {
            headset?.Dispose();
            HeadsetModel = null;
            return false;
        }
    }

    /// <summary>Which features the headset has; needs the headset on (the dongle alone can't answer).</summary>
    async Task ProbeHeadsetAsync()
    {
        if (Headset is not { HeadsetConnected: true } h || HeadsetCaps is not null) return;
        try
        {
            bool eq = await h.HasFeatureAsync(CenturionFeature.OnboardEq) && OnboardEq.IsSupportedLayout(await h.GetEqFrequenciesAsync());
            HeadsetCaps = new HeadsetCapabilities(
                Battery: await h.HasFeatureAsync(CenturionFeature.BatterySoc),
                Sleep: await h.HasFeatureAsync(CenturionFeature.AutoSleep),
                Sidetone: await h.HasFeatureAsync(CenturionFeature.Sidetone),
                NoiseReduction: await h.HasFeatureAsync(CenturionFeature.MicSnr),
                Eq: eq);
        }
        catch (Exception) { /* headset went off mid-probe; retried on the next connection event */ }
    }

    async Task PollHeadsetBatteryAsync()
    {
        if (Headset is not { HeadsetConnected: true } h || HeadsetCaps?.Battery != true) return;
        try
        {
            HeadsetBattery = await h.GetBatteryAsync();
            CheckLow("Headset", HeadsetBattery.Percent, HeadsetBattery.Charging);
        }
        catch (Exception) { HeadsetBattery = null; }
        Raise(() => Changed?.Invoke());
    }

    void DropHeadset()
    {
        var h = Headset;
        Headset = null;
        HeadsetModel = null;
        HeadsetCaps = null;
        HeadsetBattery = null;
        h?.Dispose();
        Raise(() => Changed?.Invoke());
    }

    readonly HashSet<string> _warned = [];

    void CheckLow(string device, int percent, bool charging)
    {
        if (charging || percent > _settings.LowBatteryNotifyPercent) { _warned.Remove(device); return; }
        if (_warned.Add(device)) Raise(() => Notice?.Invoke($"{device} battery is at {percent}% — time to charge."));
    }

    void Raise(Action a) => _ui.Post(_ => a(), null);

    public void Dispose()
    {
        _timer.Dispose();
        Mouse?.Dispose();
        Headset?.Dispose();
    }
}
