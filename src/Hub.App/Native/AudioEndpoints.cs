using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hub.App.Native;

public sealed record AudioEndpoint(string Id, string Guid, string Name, bool IsRender, bool Active, bool HasEqualizerApo);

/// <summary>
/// Audio endpoints read from HKLM\...\MMDevices (read-only), plus setting Windows' default device
/// through the long-standing IPolicyConfig COM interface (the same one EarTrumpet and the Windows sound settings use).
/// </summary>
public static class AudioEndpoints
{
    const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    const string DeviceDesc = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    const string InterfaceName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    const string EqualizerApoClsid = "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}";
    const int DeviceStateActive = 1;

    public static List<AudioEndpoint> List()
    {
        var result = new List<AudioEndpoint>();
        foreach (var (kind, render) in new[] { ("Render", true), ("Capture", false) })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{Root}\{kind}");
            if (key is null) continue;
            foreach (var guid in key.GetSubKeyNames())
            {
                using var dev = key.OpenSubKey(guid);
                using var props = dev?.OpenSubKey("Properties");
                using var fx = dev?.OpenSubKey("FxProperties");
                if (dev is null || props is null) continue;
                var desc = props.GetValue(DeviceDesc) as string ?? "";
                var iface = props.GetValue(InterfaceName) as string ?? "";
                bool apo = fx is not null && fx.GetValueNames().Any(n =>
                    fx.GetValue(n) is string s && s.Contains(EqualizerApoClsid, StringComparison.OrdinalIgnoreCase));
                int state = dev.GetValue("DeviceState") is int st ? st : 0;
                result.Add(new AudioEndpoint($"{{0.0.{(render ? 0 : 1)}.00000000}}.{guid}", guid,
                    string.IsNullOrEmpty(iface) ? desc : $"{desc} ({iface})", render, (state & 0xF) == DeviceStateActive, apo));
            }
        }
        return result;
    }

    public enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    public static void SetDefault(string endpointId, params Role[] roles)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in roles)
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(endpointId, role));
        }
        finally { Marshal.ReleaseComObject(config); }
    }

    /// <summary>Id of Windows' current default playback (or recording) device for a role, or null if there is none.</summary>
    public static string? GetDefault(bool render, Role role = Role.Multimedia)
    {
        var enumerator = NewEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(render ? 0 : 1, role, out var device) != 0 || device is null) return null;
            try { return device.GetId(out var id) == 0 ? id : null; }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    /// <summary>
    /// Sets an endpoint's shared-mode sample rate (Sound panel → Default Format), keeping channels and bit depth.
    /// Returns false when the rate was already set.
    /// </summary>
    public static bool SetSampleRate(string endpointId, int rate)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        IntPtr device = IntPtr.Zero, mix = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(config.GetDeviceFormat(endpointId, 0, out device));
            Marshal.ThrowExceptionForHR(config.GetMixFormat(endpointId, out mix));
            if (Marshal.ReadInt32(device, 4) == rate && Marshal.ReadInt32(mix, 4) == rate) return false;
            var d = WithRate(device, rate);
            var m = WithRate(mix, rate);
            try { Marshal.ThrowExceptionForHR(config.SetDeviceFormat(endpointId, d, m)); }
            finally { Marshal.FreeCoTaskMem(d); Marshal.FreeCoTaskMem(m); }
            return true;
        }
        finally
        {
            if (device != IntPtr.Zero) Marshal.FreeCoTaskMem(device);
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(config);
        }
    }

    /// <summary>Copy of a WAVEFORMATEX(TENSIBLE) with nSamplesPerSec / nAvgBytesPerSec changed.</summary>
    static IntPtr WithRate(IntPtr format, int rate)
    {
        int size = 18 + Marshal.ReadInt16(format, 16);
        var bytes = new byte[size];
        Marshal.Copy(format, bytes, 0, size);
        var copy = Marshal.AllocCoTaskMem(size);
        Marshal.Copy(bytes, 0, copy, size);
        Marshal.WriteInt32(copy, 4, rate);
        Marshal.WriteInt32(copy, 8, rate * Marshal.ReadInt16(format, 12));
        return copy;
    }

    /// <summary>Hides (disables) or shows an endpoint, exactly like Disable/Enable in the Sound control panel.</summary>
    public static void SetVisible(string endpointId, bool visible)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try { Marshal.ThrowExceptionForHR(config.SetEndpointVisibility(endpointId, visible ? 1 : 0)); }
        finally { Marshal.ReleaseComObject(config); }
    }

    /// <summary>Renames an endpoint (its device description, the "Speakers" part of "Speakers (PRO X 2)").</summary>
    public static void Rename(string endpointId, string name)
    {
        var enumerator = NewEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out var device));
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(2 /* STGM_READWRITE */, out var store));
        var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 2 }; // PKEY_Device_DeviceDesc
        var value = new PropVariant { VarType = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(name) };
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid FormatId; public int PropertyId; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public short VarType; short r1, r2, r3; public IntPtr Pointer; IntPtr p2; }

    // MMDeviceEnumerator is a per-process singleton and .NET caches its wrapper: creating it through a [ComImport]
    // class would make the first file that creates it own the wrapper type, and the other file's cast would fail.
    // Creating it by CLSID and casting to the interface works from any file.
    static IMMDeviceEnumerator NewEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, Role role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate();
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(); int GetAt(); int GetValue();
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, out IntPtr format);
        int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(); int SetProcessingPeriod(); int GetShareMode(); int SetShareMode();
        int GetPropertyValue(); int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
