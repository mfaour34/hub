using Hub.Core.Razer;

namespace Hub.Core.Devices;

/// <summary>A labelled button on the Customize diagram. <paramref name="Top"/> is in px within the 440 px stage.</summary>
public sealed record ButtonCallout(byte Id, string Name, bool Left, int Top);

/// <summary>
/// What Hub knows about a mouse model. <paramref name="Render"/> is an image prefix in wwwroot
/// (render-{color}.png) for models that have artwork Hub may ship; null means the generic silhouette.
/// </summary>
public sealed record MouseModel(
    string Name, string Brand, IReadOnlyList<ushort> WiredPids, IReadOnlyList<ushort> DonglePids,
    string? Render, IReadOnlyList<string> Colors, IReadOnlyList<ButtonCallout> Buttons, string WirelessName,
    byte? TransactionId, PollingScheme? DonglePolling, PollingScheme? WiredPolling, bool Known = true)
{
    public string FullName => $"{Brand} {Name}";
}

public sealed record HeadsetModel(string Name, string Brand, ushort Pid, string? Render, IReadOnlyList<string> Colors,
    string WirelessName, bool Known = true)
{
    public string FullName => $"{Brand} {Name}";
}

/// <summary>
/// The devices Hub was built and verified against, plus generic fallbacks for other Razer mice and Logitech
/// Centurion headsets. Adding a model = adding an entry here (and its renders in wwwroot).
/// </summary>
public static class DeviceCatalog
{
    /// <summary>Firmware button ids most Razer mice share (see also the openrazer project).</summary>
    public static readonly IReadOnlyList<ButtonCallout> StandardButtons =
    [
        new(1, "Left click", true, 41), new(3, "Scroll click", true, 99), new(5, "Mouse button 5", true, 155), new(4, "Mouse button 4", true, 212),
        new(2, "Right click", false, 41), new(9, "Scroll up", false, 99), new(10, "Scroll down", false, 155),
    ];

    public static readonly IReadOnlyList<MouseModel> Mice =
    [
        new("Viper V3 Pro", "Razer", [RazerMouse.WiredProductId], [RazerMouse.DongleProductId],
            Render: null, Colors: [],
            // tops line up with the 340 px mouse picture centred in the 440 px stage: side buttons are on the left edge
            Buttons: [.. StandardButtons, new(96, "Bottom button", false, 301)],
            WirelessName: "HyperSpeed wireless", TransactionId: 0x1F,
            DonglePolling: PollingScheme.HyperPolling, WiredPolling: PollingScheme.Profile),
    ];

    public static readonly IReadOnlyList<HeadsetModel> Headsets =
    [
        new("PRO X 2 LIGHTSPEED", "Logitech", 0x0AF7, Render: null, Colors: [], WirelessName: "LIGHTSPEED wireless"),
    ];

    public static MouseModel? KnownMouse(ushort pid) => Mice.FirstOrDefault(m => m.WiredPids.Contains(pid) || m.DonglePids.Contains(pid));

    public static HeadsetModel? KnownHeadset(ushort pid) => Headsets.FirstOrDefault(h => h.Pid == pid);

    /// <summary>An unrecognised Razer mouse: protocol details are probed, every standard button is offered.</summary>
    public static MouseModel GenericMouse(ushort pid) =>
        new($"Mouse ({pid:X4})", "Razer", [], [], Render: null, Colors: [], Buttons: StandardButtons,
            WirelessName: "Wireless", TransactionId: null, DonglePolling: null, WiredPolling: null, Known: false);

    public static HeadsetModel GenericHeadset(ushort pid) =>
        new($"Headset ({pid:X4})", "Logitech", pid, Render: null, Colors: [], WirelessName: "Wireless", Known: false);
}
