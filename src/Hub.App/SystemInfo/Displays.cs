using System.Runtime.InteropServices;
using Hub.Core.Diagnostics;

namespace Hub.App.SystemInfo;

/// <summary>
/// Active monitors through the DisplayConfig API (what Windows' display settings use): EDID name, resolution,
/// current and highest refresh rate at that resolution, HDR state and the connection type.
/// </summary>
static class Displays
{
    public static IEnumerable<SpecRow> Rows()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0) yield break;
        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) yield break;

        for (int i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            var name = new TargetName { Header = Header(2, Marshal.SizeOf<TargetName>(), p.TargetAdapter, p.TargetId) };
            DisplayConfigGetDeviceInfo(ref name);
            var color = new AdvancedColor { Header = Header(9, Marshal.SizeOf<AdvancedColor>(), p.TargetAdapter, p.TargetId) };
            bool hdrKnown = DisplayConfigGetDeviceInfo(ref color) == 0;
            var source = new SourceName { Header = Header(1, Marshal.SizeOf<SourceName>(), p.SourceAdapter, p.SourceId) };
            DisplayConfigGetDeviceInfo(ref source);

            var parts = new List<string>();
            int width = 0, height = 0;
            if (p.SourceModeIdx < modeCount && modes[p.SourceModeIdx].InfoType == 1)
            {
                width = modes[p.SourceModeIdx].Width; height = modes[p.SourceModeIdx].Height;
                parts.Add($"{width}×{height}");
            }
            double hz = p.RefreshDen == 0 ? 0 : p.RefreshNum / (double)p.RefreshDen;
            int max = MaxRefresh(source.GdiDeviceName, width, height);
            if (hz > 0) parts.Add($"{hz:0.#} Hz" + (max > Math.Round(hz) ? $" (max {max} Hz)" : ""));
            if (hdrKnown && (color.Value & 1) != 0) parts.Add((color.Value & 2) != 0 ? "HDR on" : "HDR off");
            parts.Add(Connection(p.OutputTechnology));
            var label = string.IsNullOrWhiteSpace(name.FriendlyName) ? "display" : name.FriendlyName;
            yield return new SpecRow(label, string.Join(" · ", parts));
        }
    }

    static string Connection(uint tech) => tech switch
    {
        0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 => "DisplayPort", 11 => "DisplayPort (internal)",
        0x80000000 => "internal", _ => "other",
    };

    /// <summary>Highest refresh rate Windows offers at the current resolution.</summary>
    static int MaxRefresh(string gdiName, int width, int height)
    {
        if (string.IsNullOrEmpty(gdiName)) return 0;
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        int max = 0;
        for (int i = 0; EnumDisplaySettingsW(gdiName, i, ref mode); i++)
            if (mode.PelsWidth == width && mode.PelsHeight == height) max = Math.Max(max, mode.DisplayFrequency);
        return max;
    }

    static DeviceInfoHeader Header(int type, int size, Luid adapter, uint id) => new() { Type = type, Size = (uint)size, Adapter = adapter, Id = id };

    const uint QdcOnlyActivePaths = 2;

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo
    {
        public Luid SourceAdapter; public uint SourceId; public uint SourceModeIdx; public uint SourceFlags;
        public Luid TargetAdapter; public uint TargetId; public uint TargetModeIdx; public uint OutputTechnology;
        public uint Rotation; public uint Scaling; public uint RefreshNum; public uint RefreshDen;
        public uint ScanLineOrdering; public int TargetAvailable; public uint TargetFlags;
        public uint Flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO; only the source-mode fields of the 48-byte union are read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct ModeInfo
    {
        [FieldOffset(0)] public int InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid Adapter;
        [FieldOffset(16)] public int Width;
        [FieldOffset(20)] public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceInfoHeader { public int Type; public uint Size; public Luid Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TargetName
    {
        public DeviceInfoHeader Header;
        public uint Flags; public uint OutputTechnology; public ushort EdidManufacturer; public ushort EdidProduct; public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
    }

    /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO: bit 0 = HDR supported, bit 1 = HDR on.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct AdvancedColor { public DeviceInfoHeader Header; public uint Value; public int ColorEncoding; public uint BitsPerChannel; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra; public int Fields;
        public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels; public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode devMode);
}
