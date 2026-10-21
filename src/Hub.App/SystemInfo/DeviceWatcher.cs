using System.Runtime.InteropServices;
using Hub.App.Native;
using Hub.Core.Diagnostics;

namespace Hub.App.SystemInfo;

/// <param name="Id">Device instance id (USB\VID_…&amp;PID_…\serial).</param>
public sealed record ConnectedDevice(string Id, string Name, string Kind);

/// <summary>
/// The System page's devices pane: USB devices plugged in now, and a 7-day timeline of connects and disconnects.
/// Windows calls Hub when a USB device arrives or leaves (no polling). Only whole devices count: their interfaces
/// (a keyboard's media keys, a headset's HID controls) and USB hubs are left out.
/// </summary>
/// <param name="knownKind">Hub's own name for a device it manages ("mouse", "headset"), else null.</param>
public sealed class DeviceWatcher(string logPath, Func<string, string?> knownKind) : IDisposable
{
    static readonly Guid UsbDeviceInterface = new("a5dcbf10-6530-11d2-901f-00c04fb951ed"); // GUID_DEVINTERFACE_USB_DEVICE

    IntPtr _registration;
    NotifyCallback? _callback; // kept alive while Windows holds the pointer

    public DeviceActivityLog Log { get; } = new(logPath);

    /// <summary>Raised (on a background thread) after a connect or disconnect was logged.</summary>
    public event Action? Changed;

    public void Start()
    {
        _callback = OnNotify;
        var filter = new NotifyFilter { Size = (uint)Marshal.SizeOf<NotifyFilter>(), FilterType = 0 /* DEVICEINTERFACE */, ClassGuid = UsbDeviceInterface };
        if (CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out _registration) != 0) _registration = IntPtr.Zero;
    }

    /// <summary>Whole USB devices present now, by the names they report.</summary>
    public List<ConnectedDevice> Connected() =>
        DeviceTree.PresentDevices("USB").Where(IsWholeDevice).Select(id => Describe(id, phantom: false))
            .Where(d => d is not null).Select(d => d!).OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    int OnNotify(IntPtr handle, IntPtr context, int action, IntPtr data, uint size)
    {
        // CM_NOTIFY_EVENT_DATA: FilterType, Reserved, then the interface class GUID and its symbolic link
        if (action is not (0 or 1)) return 0; // DEVICEINTERFACEARRIVAL / DEVICEINTERFACEREMOVAL
        var link = Marshal.PtrToStringUni(data + 24) ?? "";
        var id = DeviceKinds.InstanceIdFromInterfacePath(link);
        bool arrived = action == 0;
        // the callback must return quickly; arrivals also wait a moment so the device's children (its classes) exist
        Task.Run(async () =>
        {
            try
            {
                if (!IsWholeDevice(id)) return;
                if (arrived) await Task.Delay(1500);
                if (Describe(id, phantom: !arrived) is not { } device) return;
                Log.Add(new DeviceEvent(DateTimeOffset.Now, arrived ? "connected" : "disconnected", device.Name, device.Kind, id), DateTimeOffset.Now);
                Changed?.Invoke();
            }
            catch (Exception) { /* a device that vanished mid-read: nothing to log */ }
        });
        return 0;
    }

    static bool IsWholeDevice(string id) =>
        id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    ConnectedDevice? Describe(string id, bool phantom)
    {
        var name = DeviceTree.BusReportedName(id, phantom) ?? DeviceTree.DisplayName(id, phantom) ?? id;
        var classes = DeviceTree.ClassesWithChildren(id, phantom);
        if (classes.FirstOrDefault() is "USB" && name.Contains("hub", StringComparison.OrdinalIgnoreCase)) return null; // hubs
        var kind = knownKind(name) ?? KindFromName(name) ?? DeviceKinds.FromClasses(classes);
        return new ConnectedDevice(id, name, kind);
    }

    /// <summary>Names that say what they are win over setup classes (a mouse also has a keyboard collection).</summary>
    static string? KindFromName(string name)
    {
        foreach (var (word, kind) in new[] { ("mouse", "mouse"), ("keyboard", "keyboard"), ("headset", "headset"), ("gamepad", "controller"), ("webcam", "camera") })
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return kind;
        return null;
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) CM_Unregister_Notification(_registration);
        _registration = IntPtr.Zero;
    }

    delegate int NotifyCallback(IntPtr handle, IntPtr context, int action, IntPtr data, uint size);

    /// <summary>CM_NOTIFY_FILTER for a device-interface class (the union is 400 bytes: WCHAR[200] instance id).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [DllImport("cfgmgr32.dll")] static extern int CM_Register_Notification(ref NotifyFilter filter, IntPtr context, NotifyCallback callback, out IntPtr handle);
    [DllImport("cfgmgr32.dll")] static extern int CM_Unregister_Notification(IntPtr handle);
}
