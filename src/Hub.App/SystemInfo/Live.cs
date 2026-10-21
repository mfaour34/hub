using System.Runtime.InteropServices;

namespace Hub.App.SystemInfo;

/// <param name="CpuLoad">Percent of all cores busy.</param>
/// <param name="GpuTempC">Null where the driver doesn't report it (needs WDDM 2.4+; Task Manager shows the same value).</param>
/// <param name="GpuLoad">Percent busy of the GPU's 3D engines (Task Manager's GPU "3D" graph).</param>
public sealed record LiveReading(double? CpuLoad, double? CpuMhz, double? GpuTempC, double? GpuLoad);

/// <summary>
/// CPU load and clock, GPU temperature and load, read on request (the System and Overview pages ask every 2 s while
/// visible). No driver: the GPU numbers come from the graphics kernel's adapter performance data.
/// </summary>
public static class Live
{
    public static LiveReading Read()
    {
        double? load = null, cpuMhz = null, gpuTemp = null, gpuLoad = null;
        try { (load, cpuMhz, gpuLoad) = Counters(); } catch (Exception) { }
        try { gpuTemp = GpuTemperature(); } catch (Exception) { }
        return new LiveReading(load, cpuMhz, gpuTemp, gpuLoad);
    }

    /// <summary>
    /// Task Manager's numbers: "% Processor Utility" for load, and base frequency × "% Processor Performance" for the
    /// clock (Windows' own current-MHz figure stays at the base clock on Ryzen); the 3D engines' utilisation for the
    /// GPU. Two samples 250 ms apart.
    /// </summary>
    static (double? Load, double? Mhz, double? GpuLoad) Counters()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return (null, null, null);
        try
        {
            const string root = @"\Processor Information(_Total)\";
            PdhAddEnglishCounterW(query, root + "% Processor Utility", IntPtr.Zero, out var utility);
            PdhAddEnglishCounterW(query, root + "% Processor Performance", IntPtr.Zero, out var performance);
            PdhAddEnglishCounterW(query, root + "Processor Frequency", IntPtr.Zero, out var frequency);
            PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out var gpu);
            PdhCollectQueryData(query);
            Thread.Sleep(250);
            PdhCollectQueryData(query);
            double? load = Value(utility) is { } u ? Math.Clamp(u, 0, 100) : null;
            double? mhz = Value(frequency) is { } f && Value(performance) is { } perf ? f * perf / 100 : null;
            double? gpuLoad = Sum(gpu) is { } g ? Math.Clamp(g, 0, 100) : null;
            return (load, mhz, gpuLoad);
        }
        finally { PdhCloseQuery(query); }

        // a wildcard counter has one instance per process using the engine; their sum is the engine's load
        static double? Sum(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return null;
            uint size = 0, count = 0;
            PdhGetFormattedCounterArrayW(counter, 0x200, ref size, ref count, IntPtr.Zero);
            if (size == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, 0x200, ref size, ref count, buffer) != 0) return null;
                double sum = 0;
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhItem>(buffer + i * Marshal.SizeOf<PdhItem>());
                    if (item.Value.Status == 0) sum += item.Value.Value;
                }
                return sum;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        static double? Value(IntPtr counter) =>
            counter != IntPtr.Zero && PdhGetFormattedCounterValue(counter, 0x200 /* PDH_FMT_DOUBLE */, out _, out var v) == 0 && v.Status == 0 ? v.Value : null;
    }

    /// <summary>Temperature of the GPU with the fastest memory (the discrete card on a desktop).</summary>
    static double? GpuTemperature()
    {
        var enumArgs = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.Count == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)enumArgs.Count * Marshal.SizeOf<AdapterInfo>());
        try
        {
            enumArgs.Adapters = buffer;
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0) return null;
            (double? temp, ulong memFreq) best = (null, 0);
            for (int i = 0; i < enumArgs.Count; i++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(buffer + i * Marshal.SizeOf<AdapterInfo>());
                try
                {
                    var perf = new AdapterPerfData();
                    if (!Query(adapter.Handle, 62 /* KMTQAITYPE_ADAPTERPERFDATA */, ref perf)) continue;
                    if (perf.MaxMemoryFrequency < best.memFreq) continue;
                    best = (perf.Temperature > 0 ? perf.Temperature / 10.0 : null, perf.MaxMemoryFrequency);
                }
                finally
                {
                    var close = new CloseAdapter { Handle = adapter.Handle };
                    D3DKMTCloseAdapter(ref close);
                }
            }
            return best.temp;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static bool Query<T>(uint adapter, int type, ref T data) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, ptr, false);
            var q = new QueryAdapterInfo { Adapter = adapter, Type = type, Data = ptr, DataSize = (uint)size };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
            data = Marshal.PtrToStructure<T>(ptr);
            return true;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [StructLayout(LayoutKind.Sequential)] struct PdhValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)] struct PdhItem { public IntPtr Name; public PdhValue Value; }

    [StructLayout(LayoutKind.Sequential)] struct EnumAdapters2 { public uint Count; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct AdapterInfo { public uint Handle; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int PreciseRegions; }
    [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint Handle; }
    [StructLayout(LayoutKind.Sequential)] struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint DataSize; }

    /// <summary>D3DKMT_ADAPTER_PERFDATA: Temperature is in tenths of a degree Celsius.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PcieBandwidth;
        public uint FanRpm, Power, Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr items);
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 args);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter args);
}
