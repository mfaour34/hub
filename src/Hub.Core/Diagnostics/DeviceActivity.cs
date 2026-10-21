using System.Text.Json;

namespace Hub.Core.Diagnostics;

/// <param name="Action">"connected", "disconnected" or "reconnected" (out and back within a few seconds).</param>
/// <param name="Kind">From <see cref="DeviceKinds.FromClasses"/>: "keyboard", "mouse", "audio", …</param>
public sealed record DeviceEvent(DateTimeOffset Time, string Action, string Name, string Kind, string InstanceId);

/// <summary>
/// The System page's device timeline: 7 days of plug/unplug events as JSON lines (same shape and safety as
/// <see cref="CrashLog"/>). An unplug followed by a replug of the same device within <see cref="ReconnectWindow"/>
/// is one "reconnected" entry, the sign of a loose cable or a port dropping out.
/// </summary>
public sealed class DeviceActivityLog(string path)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    public static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(5);

    readonly object _gate = new();
    List<DeviceEvent>? _events;

    public void Add(DeviceEvent e, DateTimeOffset now)
    {
        lock (_gate)
        {
            var list = Events();
            var last = list.LastOrDefault(x => x.InstanceId == e.InstanceId);
            if (e.Action == "connected" && last is { Action: "disconnected" } && e.Time - last.Time <= ReconnectWindow)
                list[list.LastIndexOf(last)] = e with { Action = "reconnected" };
            else
                list.Add(e);
            list.RemoveAll(x => x.Time < now - Window);
            Write(list);
        }
    }

    /// <summary>The last 7 days, newest first.</summary>
    public IReadOnlyList<DeviceEvent> Recent(DateTimeOffset now)
    {
        lock (_gate) return Events().Where(e => e.Time >= now - Window).OrderByDescending(e => e.Time).ToList();
    }

    List<DeviceEvent> Events()
    {
        if (_events is not null) return _events;
        _events = [];
        if (!File.Exists(path)) return _events;
        foreach (var line in File.ReadLines(path))
        {
            try { if (JsonSerializer.Deserialize<DeviceEvent>(line) is { InstanceId: not null } e) _events.Add(e); }
            catch (JsonException) { /* a line cut short by a crash */ }
        }
        _events.Sort((a, b) => a.Time.CompareTo(b.Time));
        return _events;
    }

    void Write(List<DeviceEvent> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, list.Select(e => JsonSerializer.Serialize(e)));
        File.Move(tmp, path, overwrite: true);
    }
}

public static class DeviceKinds
{
    /// <summary>
    /// Setup classes, most telling first. Gaming mice also carry a keyboard collection (for macro keys), so a mouse
    /// can read as a keyboard here; the caller corrects that with what it knows (Hub's own mouse, the device name).
    /// </summary>
    static readonly (string Kind, string[] Classes)[] Order =
    [
        ("keyboard", ["Keyboard"]),
        ("mouse", ["Mouse"]),
        ("controller", ["XnaComposite", "XboxComposite", "GameController"]),
        ("camera", ["Camera", "Image"]),
        ("audio", ["MEDIA", "AudioEndpoint"]),
        ("storage", ["DiskDrive", "WPD", "Volume", "CDROM"]),
        ("network", ["Net"]),
        ("bluetooth", ["Bluetooth"]),
        ("printer", ["Printer"]),
    ];

    /// <summary>
    /// The kind of a USB device from the setup classes of it (first) and its children. A device whose own class is
    /// telling keeps it: a Bluetooth adapter has audio children, but it's a Bluetooth adapter.
    /// </summary>
    public static string FromClasses(IEnumerable<string> classes)
    {
        var list = classes.ToList();
        if (list.FirstOrDefault() is { } own && Order.FirstOrDefault(o => o.Classes.Contains(own, StringComparer.OrdinalIgnoreCase)).Kind is { } ownKind)
            return ownKind;
        var set = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Order.FirstOrDefault(o => o.Classes.Any(set.Contains)).Kind ?? "device";
    }

    /// <summary>
    /// The device instance id behind a device-interface path:
    /// \\?\USB#VID_31E3&amp;PID_1322#A02B2452#{guid} → USB\VID_31E3&amp;PID_1322\A02B2452.
    /// </summary>
    public static string InstanceIdFromInterfacePath(string path)
    {
        var p = path.StartsWith(@"\\?\") ? path[4..] : path;
        int guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid >= 0) p = p[..guid];
        return p.Replace('#', '\\').ToUpperInvariant();
    }
}
