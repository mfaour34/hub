using System.Runtime.InteropServices;

namespace Hub.App.Native;

/// <summary>Device-tree lookups through cfgmgr32 (the Device Manager API), no admin rights needed.</summary>
static class DeviceTree
{
    /// <summary>
    /// The product string a device reports over its bus ("Acme TKL" rather than "HID Keyboard Device"). HID
    /// collections don't carry it, their USB parent does, so this walks up a few levels.
    /// </summary>
    /// <param name="phantom">Also find a device that was just unplugged (its node stays known to Windows).</param>
    public static string? BusReportedName(string instanceId, bool phantom = false)
    {
        if (CM_Locate_DevNodeW(out uint node, instanceId, phantom ? LocatePhantom : 0) != 0) return null;
        for (int depth = 0; depth < 4; depth++)
        {
            if (StringProperty(node, BusReportedDeviceDesc) is { Length: > 0 } name) return name;
            if (CM_Get_Parent(out node, node, 0) != 0) return null;
        }
        return null;
    }

    /// <summary>The name Device Manager shows (friendly name, else description).</summary>
    public static string? DisplayName(string instanceId, bool phantom = false) =>
        CM_Locate_DevNodeW(out uint node, instanceId, phantom ? LocatePhantom : 0) != 0 ? null
            : StringProperty(node, FriendlyName) ?? StringProperty(node, DeviceDesc);

    /// <summary>Setup classes of a device and its children ("USB", "HIDClass", "Keyboard", …), two levels down.</summary>
    public static List<string> ClassesWithChildren(string instanceId, bool phantom = false)
    {
        var classes = new List<string>();
        if (CM_Locate_DevNodeW(out uint node, instanceId, phantom ? LocatePhantom : 0) != 0) return classes;
        void Walk(uint n, int depth)
        {
            if (StringProperty(n, Class) is { } c) classes.Add(c);
            // HID game controllers have no class of their own; their description says what they are
            if (StringProperty(n, DeviceDesc)?.Contains("game controller", StringComparison.OrdinalIgnoreCase) == true) classes.Add("GameController");
            if (depth == 2 || CM_Get_Child(out uint child, n, 0) != 0) return;
            do Walk(child, depth + 1); while (CM_Get_Sibling(out child, child, 0) == 0);
        }
        Walk(node, 0);
        return classes;
    }

    /// <summary>Instance ids of present devices on one enumerator ("USB", "BTHENUM").</summary>
    public static List<string> PresentDevices(string enumerator)
    {
        const uint flags = 0x1 /* FILTER_ENUMERATOR */ | 0x100 /* FILTER_PRESENT */;
        if (CM_Get_Device_ID_List_SizeW(out uint length, enumerator, flags) != 0 || length == 0) return [];
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(enumerator, buffer, length, flags) != 0) return [];
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    static string? StringProperty(uint node, DevPropKey key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(node, ref key, out _, IntPtr.Zero, ref size, 0);
        if (size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return CM_Get_DevNode_PropertyW(node, ref key, out uint type, buffer, ref size, 0) == 0 && type == 0x12 /* DEVPROP_TYPE_STRING */
                ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] struct DevPropKey { public Guid Fmtid; public uint Pid; }

    static readonly DevPropKey BusReportedDeviceDesc = new() { Fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), Pid = 4 };
    static readonly DevPropKey DeviceDesc = new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 2 };
    static readonly DevPropKey Class = new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 9 };
    static readonly DevPropKey FriendlyName = new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
    const uint LocatePhantom = 1;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Child(out uint child, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Sibling(out uint sibling, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint type, IntPtr buffer, ref uint size, uint flags);
}
