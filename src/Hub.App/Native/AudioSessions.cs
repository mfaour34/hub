using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hub.App.Native;

public sealed record AudioSession(int ProcessId, string ProcessName, string EndpointGuid, bool Active);

/// <summary>
/// Which apps are playing audio and on which device (Core Audio session API), and per-app output routing through
/// Windows' AudioPolicyConfig — the same mechanism as Settings → Volume mixer and EarTrumpet.
/// </summary>
public static class AudioSessions
{
    const int ERender = 0, DeviceStateActive = 1, ClsctxAll = 23;
    static readonly Guid IidSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <param name="endpointGuid">Only this playback device's sessions (all devices when null).</param>
    /// <param name="names">Process-name cache by pid, so a poll doesn't open every process again.</param>
    public static List<AudioSession> List(string? endpointGuid = null, Dictionary<int, string>? names = null)
    {
        var result = new List<AudioSession>();
        var enumerator = NewEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out var devices));
        devices.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            devices.Item(i, out var device);
            device.GetId(out var id);
            var guid = id[(id.LastIndexOf('.') + 1)..];
            if (endpointGuid is not null && !guid.Equals(endpointGuid, StringComparison.OrdinalIgnoreCase)) { Marshal.ReleaseComObject(device); continue; }
            var iid = IidSessionManager2;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj) != 0) continue;
            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out var sessions) != 0) continue;
            sessions.GetCount(out int n);
            for (int s = 0; s < n; s++)
            {
                sessions.GetSession(s, out var control);
                if (control is not IAudioSessionControl2 c2) continue;
                try
                {
                    if (c2.IsSystemSoundsSession() == 0) continue; // S_OK means it is the system sounds session
                    c2.GetProcessId(out int pid);
                    c2.GetState(out int state);
                    if (names is null || !names.TryGetValue(pid, out var name))
                    {
                        try { name = Process.GetProcessById(pid).ProcessName; } catch (ArgumentException) { continue; }
                        if (names is not null) names[pid] = name;
                    }
                    result.Add(new AudioSession(pid, name, guid, state == 1));
                }
                finally { Marshal.ReleaseComObject(control); }
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
        }
        Marshal.ReleaseComObject(devices);
        Marshal.ReleaseComObject(enumerator);
        return result;
    }

    /// <summary>Routes one process's output to an endpoint (both Console and Multimedia roles, like the Volume mixer).</summary>
    public static void SetAppOutput(int processId, string? endpointId)
    {
        var factory = Factory();
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (endpointId is not null)
            {
                var full = $@"\\?\SWD#MMDEVAPI#{endpointId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}";
                Marshal.ThrowExceptionForHR(WindowsCreateString(full, full.Length, out hstring));
            }
            Marshal.ThrowExceptionForHR(factory.SetPersistedDefaultAudioEndpoint(processId, ERender, 0, hstring));
            Marshal.ThrowExceptionForHR(factory.SetPersistedDefaultAudioEndpoint(processId, ERender, 1, hstring));
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>The app's own output override, or null when it follows the Windows default.</summary>
    public static string? GetAppOutput(int processId)
    {
        var factory = Factory();
        try
        {
            Marshal.ThrowExceptionForHR(factory.GetPersistedDefaultAudioEndpoint(processId, ERender, 1, out var h));
            if (h == IntPtr.Zero) return null;
            var s = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out _));
            WindowsDeleteString(h);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    [DllImport("combase.dll", PreserveSig = true)]
    static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);

    static IAudioPolicyConfigFactory Factory()
    {
        const string cls = "Windows.Media.Internal.AudioPolicyConfig";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var hcls));
        try
        {
            var iid = typeof(IAudioPolicyConfigFactory).GUID; // Windows 10 21H2+ / Windows 11 layout
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hcls, ref iid, out var factory));
            return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(factory);
        }
        finally { WindowsDeleteString(hcls); }
    }

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    // IInspectable is declared as IUnknown with its three methods first; .NET 8 has no built-in WinRT interop.
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfigFactory
    {
        int GetIids(); int GetRuntimeClassName(); int GetTrustLevel();
        int add_CtxVolumeChange(); int remove_CtxVolumeChanged(); int add_RingerVibrateStateChanged();
        int remove_RingerVibrateStateChange(); int SetVolumeGroupGainForId(); int GetVolumeGroupGainForId();
        int GetActiveVolumeGroupForEndpointId(); int GetVolumeGroupsForEndpoint(); int GetCurrentVolumeContext();
        int SetVolumeGroupMuteForId(); int GetVolumeGroupMuteForId(); int SetRingerVibrateState();
        int GetRingerVibrateState(); int SetPreferredChatApplication(); int ResetPreferredChatApplication();
        int GetPreferredChatApplication(); int GetCurrentChatApplications(); int add_ChatContextChanged();
        int remove_ChatContextChanged();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(int processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(int processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    // MMDeviceEnumerator is a per-process singleton and .NET caches its wrapper: creating it through a [ComImport]
    // class would make the first file that creates it own the wrapper type, and the other file's cast would fail.
    // Creating it by CLSID and casting to the interface works from any file.
    static IMMDeviceEnumerator NewEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(); int GetSimpleAudioVolume();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(); int SetDisplayName(); int GetIconPath(); int SetIconPath(); int GetGroupingParam();
        int SetGroupingParam(); int RegisterAudioSessionNotification(); int UnregisterAudioSessionNotification();
        int GetSessionIdentifier(); int GetSessionInstanceIdentifier();
        int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        int SetDuckingPreference();
    }
}
