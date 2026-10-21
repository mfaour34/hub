using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Hub.App.Native;
using Hub.Core.Diagnostics;
using Microsoft.Win32;

namespace Hub.App.SystemInfo;

/// <summary>A device Hub talks to, for the peripherals section.</summary>
public sealed record DeviceInfo(string? Name, string? Firmware);

/// <summary>
/// The System page's read-only PC inventory: WMI, the registry and DisplayConfig, all readable without admin rights.
/// Read when the page opens; each section is read on its own so one missing source doesn't hide the rest.
/// </summary>
public static class Specs
{
    public static List<SpecSection> Read(DeviceInfo mouse, DeviceInfo headset)
    {
        var sections = new List<SpecSection>();
        void Add(string title, Func<IEnumerable<SpecRow>> rows)
        {
            try { sections.Add(new SpecSection(title, rows().ToList())); }
            catch (Exception) { /* source unavailable here (VM, older Windows, WMI repository broken) */ }
        }
        Add("cpu", Cpu);
        Add("gpu", Gpu);
        Add("memory", Memory);
        Add("motherboard", Board);
        Add("storage", Storage);
        Add("displays", Displays.Rows);
        Add("windows", WindowsRows);
        Add("peripherals", () => Peripherals(mouse, headset));
        Add("network", Network);
        return sections;
    }

    // ---------------- sections ----------------

    static IEnumerable<SpecRow> Cpu()
    {
        foreach (var p in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            yield return new("model", Str(p["Name"]).Trim());
            yield return new("cores", $"{p["NumberOfCores"]} cores · {p["NumberOfLogicalProcessors"]} threads");
            yield return new("base clock", $"{Convert.ToDouble(p["MaxClockSpeed"]) / 1000:0.0#} GHz");
        }
    }

    static IEnumerable<SpecRow> Gpu()
    {
        var vram = DisplayAdapterMemory();
        foreach (var v in Query("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
        {
            var name = Str(v["Name"]);
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
            yield return new("model", name);
            if (vram.TryGetValue(name, out var bytes) && bytes > 0) yield return new("vram", Size(bytes));
            yield return new("driver", $"{v["DriverVersion"]} ({WmiDate(v["DriverDate"])})");
        }
    }

    static IEnumerable<SpecRow> Memory()
    {
        var sticks = Query("SELECT Capacity, ConfiguredClockSpeed, Speed, DeviceLocator, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
        if (sticks.Count == 0) yield break;
        ulong total = sticks.Aggregate(0UL, (s, m) => s + Convert.ToUInt64(m["Capacity"]));
        int slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => Convert.ToInt32(a["MemoryDevices"] ?? 0));
        var speed = sticks.Select(m => Convert.ToInt32(m["ConfiguredClockSpeed"] ?? m["Speed"] ?? 0)).Max();
        yield return new("total", Size(total));
        if (speed > 0) yield return new("speed", $"{speed} MT/s");
        var each = sticks.GroupBy(m => Convert.ToUInt64(m["Capacity"])).Select(g => $"{g.Count()} × {Size(g.Key)}");
        yield return new("sticks", $"{string.Join(" + ", each)}" + (slots > 0 ? $" in {sticks.Count} of {slots} slots" : "")
            + $" ({string.Join(", ", sticks.Select(m => Str(m["DeviceLocator"])))})");
        var part = sticks.Select(m => $"{Str(m["Manufacturer"]).Trim()} {Str(m["PartNumber"]).Trim()}".Trim()).Distinct().ToList();
        if (part.Count == 1 && part[0].Length > 0) yield return new("part", part[0]);
    }

    static IEnumerable<SpecRow> Board()
    {
        foreach (var b in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            yield return new("model", $"{Str(b["Manufacturer"]).Trim()} {Str(b["Product"]).Trim()}");
        foreach (var b in Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            yield return new("bios", $"{b["SMBIOSBIOSVersion"]} ({WmiDate(b["ReleaseDate"])})");
    }

    static IEnumerable<SpecRow> Storage()
    {
        foreach (var d in Query("SELECT FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            int bus = Convert.ToInt32(d["BusType"] ?? 0), media = Convert.ToInt32(d["MediaType"] ?? 0);
            string kind = bus == 17 ? "NVMe" : media == 3 ? "HDD" : media == 4 ? (bus == 11 ? "SATA SSD" : "SSD") : bus == 7 ? "USB" : "disk";
            yield return new(kind, $"{Str(d["FriendlyName"]).Trim()} · {Size(Convert.ToUInt64(d["Size"]))}");
        }
        foreach (var v in Query("SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
        {
            ulong size = Convert.ToUInt64(v["Size"] ?? 0UL), free = Convert.ToUInt64(v["FreeSpace"] ?? 0UL);
            if (size == 0) continue;
            var label = Str(v["VolumeName"]);
            yield return new($"drive {Str(v["DeviceID"]).TrimEnd(':')}{(label.Length > 0 ? $" ({label})" : "")}", $"{Size(free)} free of {Size(size)}");
        }
    }

    static IEnumerable<SpecRow> WindowsRows()
    {
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        foreach (var os in Query("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"))
        {
            yield return new("edition", Str(os["Caption"]).Replace("Microsoft ", ""));
            yield return new("version", $"{nt?.GetValue("DisplayVersion")} (build {os["BuildNumber"]}.{nt?.GetValue("UBR")})");
        }
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        yield return new("uptime", up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min");
        if (ActivePowerPlan() is { } plan) yield return new("power plan", plan);
    }

    static IEnumerable<SpecRow> Peripherals(DeviceInfo mouse, DeviceInfo headset)
    {
        static string Dev(DeviceInfo d) => d.Firmware is { Length: > 0 } fw ? $"{d.Name} (firmware {fw})" : d.Name!;
        if (mouse.Name is not null) yield return new("mouse", Dev(mouse));
        if (headset.Name is not null) yield return new("headset", Dev(headset));
        // mice and headsets also expose keyboard collections (for macro keys); list only real keyboards
        bool IsHubDevice(string n) => (mouse.Name is { } m && n.Contains(m, StringComparison.OrdinalIgnoreCase))
            || (headset.Name is { } h && n.Contains(h, StringComparison.OrdinalIgnoreCase));
        foreach (var k in PnpNames("Keyboard").Where(n => !IsHubDevice(n))) yield return new("keyboard", k);
        foreach (var c in PnpNames("XnaComposite").Concat(PnpNames("XboxComposite")).Concat(GameControllers())) yield return new("controller", c);
        foreach (var c in PnpNames("Camera").Concat(PnpNames("Image"))) yield return new("camera", c);
        var endpoints = AudioEndpoints.List().Where(e => e.Active && !IsVirtualAudio(e.Name)).ToList();
        foreach (var e in endpoints.Where(e => !e.IsRender)) yield return new("microphone", e.Name);
        foreach (var e in endpoints.Where(e => e.IsRender)) yield return new("playback", e.Name);
    }

    static IEnumerable<SpecRow> Network()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (n.GetIPProperties().GatewayAddresses.Count == 0) continue; // only adapters that reach the internet
            var kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
            var speed = n.Speed >= 1_000_000_000 ? $"{n.Speed / 1e9:0.#} Gbps" : $"{n.Speed / 1e6:0} Mbps";
            yield return new(kind, $"{n.Description} · {speed}");
        }
    }

    // ---------------- helpers ----------------

    static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        // a stuck WMI service would otherwise block forever instead of failing the section
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>().ToList();
    }

    static string Str(object? o) => o?.ToString() ?? "";

    /// <summary>Software devices (Hub's own sink, Steam, Sonar, VoiceMeeter) aren't hardware worth listing.</summary>
    static bool IsVirtualAudio(string name) =>
        new[] { "Hub Audio", "Steam Streaming", "Sonar", "Voicemeeter", "VB-Audio" }.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase));

    static string Size(ulong bytes) => bytes >= 1UL << 40 ? $"{bytes / (double)(1UL << 40):0.##} TB"
        : bytes >= 1UL << 30 ? $"{bytes / (double)(1UL << 30):0} GB" : $"{bytes / (double)(1UL << 20):0} MB";

    /// <summary>WMI CIM_DATETIME ("20240919000000.000000-000") as yyyy-MM-dd.</summary>
    static string WmiDate(object? o) => Str(o) is { Length: >= 8 } s ? $"{s[..4]}-{s[4..6]}-{s[6..8]}" : "?";

    /// <summary>Dedicated VRAM per adapter name: WMI's AdapterRAM stops at 4 GB, the driver's registry value doesn't.</summary>
    static Dictionary<string, ulong> DisplayAdapterMemory()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (var sub in cls?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string name && k.GetValue("HardwareInformation.qwMemorySize") is long size)
                    result[name] = (ulong)size;
            }
            catch (Exception) { /* "Properties" subkey is protected */ }
        }
        return result;
    }

    /// <summary>Present devices of a setup class, by the name the device itself reports where Windows only has a generic one.</summary>
    static IEnumerable<string> PnpNames(string pnpClass)
    {
        var names = new List<string>();
        foreach (var d in Query($"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE PNPClass = '{pnpClass}' AND Present = TRUE"))
            names.Add(DeviceTree.BusReportedName(Str(d["DeviceID"])) ?? Str(d["Name"]));
        return names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static IEnumerable<string> GameControllers() =>
        Query("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'HIDClass' AND Present = TRUE AND Name LIKE '%game controller%'")
            .Select(d => Str(d["Name"])).Distinct();

    static string? ActivePowerPlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var guidPtr) != 0) return null;
        try
        {
            var guid = Marshal.PtrToStructure<Guid>(guidPtr);
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                return PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                    ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { LocalFree(guidPtr); }
    }

    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
}
