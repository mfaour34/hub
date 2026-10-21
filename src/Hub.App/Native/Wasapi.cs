using System.Runtime.InteropServices;

namespace Hub.App.Native;

/// <summary>
/// The WASAPI pieces Hub's audio engine needs: a shared-mode float render stream to the output device, per-process
/// loopback capture (Windows 10 2004+), endpoint volume, and MMCSS scheduling for the audio thread.
/// </summary>
public static class Wasapi
{
    public const int ShareShared = 0;
    public const uint StreamLoopback = 0x00020000, StreamEventCallback = 0x00040000;
    public const uint StreamAutoConvertPcm = 0x80000000, StreamSrcDefaultQuality = 0x08000000;
    public const uint BufferSilent = 0x2;
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    const int ClsctxAll = 23;

    /// <summary>WAVEFORMATEXTENSIBLE for interleaved 32-bit float stereo (FL, FR).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct FloatStereoFormat
    {
        public ushort Tag, Channels; public uint SampleRate, AvgBytesPerSec; public ushort BlockAlign, Bits, Size;
        public ushort ValidBits; public uint ChannelMask; public Guid SubFormat;

        public static FloatStereoFormat At(int rate) => new()
        {
            Tag = 0xFFFE, Channels = 2, SampleRate = (uint)rate, AvgBytesPerSec = (uint)rate * 8, BlockAlign = 8, Bits = 32, Size = 22,
            ValidBits = 32, ChannelMask = 0x3, SubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71"),
        };
    }

    public static IMMDevice Device(string endpointId)
    {
        var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            Marshal.ThrowExceptionForHR(e.GetDevice(endpointId, out var d));
            return d;
        }
        finally { Marshal.ReleaseComObject(e); }
    }

    public static T Activate<T>(IMMDevice device)
    {
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var o));
        return (T)o;
    }

    public static T Service<T>(IAudioClient client)
    {
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var o));
        return (T)o;
    }

    public static void Initialize(IAudioClient client, uint flags, long bufferHns, int rate)
    {
        var fmt = FloatStereoFormat.At(rate);
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<FloatStereoFormat>());
        try
        {
            Marshal.StructureToPtr(fmt, p, false);
            Marshal.ThrowExceptionForHR(client.Initialize(ShareShared, flags, bufferHns, 0, p, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// Asks for a raw stream (no Windows or vendor effects on the device, so the
    /// channel EQ is the only processing). Returns false where the device doesn't offer raw mode; the stream then plays normally.
    /// </summary>
    public static bool TryRaw(IAudioClient client)
    {
        if (client is not IAudioClient2 c2) return false;
        var props = new AudioClientProperties { Size = (uint)Marshal.SizeOf<AudioClientProperties>(), Category = 0, Options = 1 /* RAW */ };
        return c2.SetClientProperties(ref props) == 0;
    }

    // ---------------- process loopback ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct LoopbackParams { public int Type; public uint TargetPid; public int Mode; }

    [StructLayout(LayoutKind.Sequential)]
    struct BlobVariant { public ushort Vt; ushort r1, r2, r3; public uint Size; public IntPtr Data; }

    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Hr;
        public object? Client;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try { op.GetActivateResult(out Hr, out var o); Client = o; }
            catch (Exception ex) { Hr = ex.HResult; }
            Done.Set();
        }
    }

    /// <summary>An audio client that captures what <paramref name="pid"/> and its child processes play, on any device.</summary>
    public static IAudioClient ProcessLoopback(int pid)
    {
        var parameters = new LoopbackParams { Type = 1 /* PROCESS_LOOPBACK */, TargetPid = (uint)pid, Mode = 0 /* include tree */ };
        IntPtr pp = Marshal.AllocHGlobal(Marshal.SizeOf<LoopbackParams>()), pv = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        try
        {
            Marshal.StructureToPtr(parameters, pp, false);
            Marshal.StructureToPtr(new BlobVariant { Vt = 65 /* VT_BLOB */, Size = (uint)Marshal.SizeOf<LoopbackParams>(), Data = pp }, pv, false);
            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", ref iid, pv, handler, out _));
            if (!handler.Done.Wait(3000)) throw new TimeoutException("Windows didn't open the app's audio in time");
            Marshal.ThrowExceptionForHR(handler.Hr);
            return (IAudioClient)handler.Client!;
        }
        finally { Marshal.FreeHGlobal(pp); Marshal.FreeHGlobal(pv); }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid iid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    // ---------------- MMCSS ----------------

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);

    [DllImport("avrt.dll")]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    // ---------------- interfaces ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientProperties { public uint Size; public int IsOffload; public int Category; public int Options; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(); int GetDefaultAudioEndpoint();
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("726778CD-F60A-4eda-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient2
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr n); int UnregisterControlChangeNotify(IntPtr n); int GetChannelCount(out uint n);
        int SetMasterVolumeLevel(float db, ref Guid ctx); int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx); int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float db); int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
}
