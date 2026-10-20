using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Hub.Core.Hid;

namespace Hub.App.Native;

public sealed record HidDeviceInfo(string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage,
    int InputLength, int OutputLength, int FeatureLength);

static partial class HidApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareReadWrite = 3, OpenExisting = 3,
        FileFlagOverlapped = 0x40000000;
    const int DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr dev);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    public static SafeFileHandle Open(string path, uint access, uint flags = 0) =>
        CreateFile(path, access, ShareReadWrite, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);

    public static List<HidDeviceInfo> Enumerate(ushort vendorId)
    {
        var result = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4)!;
                    if (!path.Contains($"vid_{vendorId:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                    using var h = Open(path, 0);
                    if (h.IsInvalid) continue;
                    var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(h, ref attrs)) continue;
                    var caps = new HIDP_CAPS();
                    if (!HidD_GetPreparsedData(h, out var pp)) continue;
                    try { HidP_GetCaps(pp, ref caps); } finally { HidD_FreePreparsedData(pp); }
                    result.Add(new HidDeviceInfo(path, attrs.VendorID, attrs.ProductID, caps.UsagePage, caps.Usage,
                        caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
}

/// <summary>Feature reports on a mouse top-level collection; a zero-access handle is sufficient.</summary>
public sealed class WinFeatureChannel : IFeatureChannel
{
    readonly SafeFileHandle _handle;

    public WinFeatureChannel(string path)
    {
        _handle = HidApi.Open(path, 0);
        if (_handle.IsInvalid) throw new IOException($"Cannot open {path} (error {Marshal.GetLastWin32Error()})");
    }

    public void SetFeature(byte[] report)
    {
        if (!HidApi.HidD_SetFeature(_handle, report, report.Length))
            throw new IOException($"HidD_SetFeature failed ({Marshal.GetLastWin32Error()})");
    }

    public byte[] GetFeature(int length)
    {
        var buf = new byte[length];
        if (!HidApi.HidD_GetFeature(_handle, buf, length))
            throw new IOException($"HidD_GetFeature failed ({Marshal.GetLastWin32Error()})");
        return buf;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Shared read/write handle on a vendor collection with a background input-report reader.</summary>
public sealed class WinReportChannel : IReportChannel
{
    readonly FileStream _stream;
    readonly int _inputLength, _outputLength;
    readonly CancellationTokenSource _cts = new();

    public event Action<byte[]>? FrameReceived;
    public event Action? Closed;

    public WinReportChannel(HidDeviceInfo info)
    {
        var handle = HidApi.Open(info.Path, HidApi.GenericRead | HidApi.GenericWrite, HidApi.FileFlagOverlapped);
        if (handle.IsInvalid) throw new IOException($"Cannot open {info.Path} (error {Marshal.GetLastWin32Error()})");
        _inputLength = info.InputLength;
        _outputLength = info.OutputLength;
        _stream = new FileStream(handle, FileAccess.ReadWrite, Math.Max(_inputLength, 64), isAsync: true);
        _ = Task.Run(ReadLoop);
    }

    public void Write(byte[] frame)
    {
        var buf = new byte[_outputLength];
        Array.Copy(frame, buf, Math.Min(frame.Length, buf.Length));
        _stream.Write(buf, 0, buf.Length);
    }

    async Task ReadLoop()
    {
        var buf = new byte[_inputLength];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                FrameReceived?.Invoke(buf[..n]);
            }
        }
        catch (Exception) when (!_cts.IsCancellationRequested) { }
        catch (OperationCanceledException) { return; }
        Closed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
    }
}
