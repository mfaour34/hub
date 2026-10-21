using System.Runtime.InteropServices;

namespace Hub.App.Native;

/// <summary>Parent process ids from a Toolhelp snapshot (.NET has no Process.ParentId).</summary>
public static class ProcessTree
{
    public static Dictionary<int, int> Parents()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[(int)e.Pid] = (int)e.ParentPid;
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>True when one of <paramref name="pid"/>'s ancestors is in <paramref name="set"/>.</summary>
    public static bool HasAncestorIn(int pid, IReadOnlyDictionary<int, int> parents, IReadOnlySet<int> set)
    {
        var seen = new HashSet<int> { pid };
        for (int p = pid; parents.TryGetValue(p, out var parent) && parent != 0 && seen.Add(parent); p = parent)
            if (set.Contains(parent)) return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, Pid; public IntPtr HeapId; public uint ModuleId, Threads, ParentPid; public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
