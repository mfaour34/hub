using System.Diagnostics;
using System.Runtime.InteropServices;
using Hub.Core.Diagnostics;

namespace Hub.App.Native;

/// <param name="Device">Name of the mouse that moved or clicked last.</param>
public sealed record RawMouseSnapshot(string? Device, PollingStats Polling, IReadOnlyList<ButtonStats> Buttons);

/// <summary>
/// The Mouse page's test session: every report from every mouse through Raw Input, timestamped as it arrives.
/// Exists only while the test runs (registration is removed on Dispose). Runs its own thread with a message-only
/// window, so the UI thread never sees the 4000–8000 messages a second; that thread only timestamps and stores.
/// </summary>
public sealed class RawMouse : IDisposable
{
    sealed class Device(long frequency)
    {
        public readonly PollingMeter Polling = new(frequency);
        public readonly ClickChatter Clicks = new(frequency);
        public long LastEvent;
        public string? Name;
    }

    readonly Dictionary<IntPtr, Device> _devices = [];
    readonly object _gate = new();
    readonly Thread _thread;
    Sink? _sink;
    double _thresholdMs = 30;

    public RawMouse()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Hub mouse test", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread.Start(ready);
        ready.Wait(2000);
    }

    /// <summary>Release-to-press gap below which a press counts as chatter.</summary>
    public double ThresholdMs
    {
        get => _thresholdMs;
        set
        {
            _thresholdMs = Math.Clamp(value, 10, 80);
            lock (_gate) foreach (var d in _devices.Values) d.Clicks.ThresholdMs = _thresholdMs;
        }
    }

    /// <summary>The numbers of the mouse used last (other mice and touchpads don't mix in).</summary>
    public RawMouseSnapshot Snapshot()
    {
        Device? active;
        lock (_gate) active = _devices.Values.MaxBy(d => d.LastEvent);
        return active is null ? new(null, PollingStats.Empty, [])
            : new(active.Name, active.Polling.Stats(), active.Clicks.Stats());
    }

    public void ResetClicks()
    {
        lock (_gate) foreach (var d in _devices.Values) d.Clicks.Reset();
    }

    void Run(object? ready)
    {
        _sink = new Sink(this);
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevInputSink, Target = _sink.Handle };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        ((ManualResetEventSlim)ready!).Set();
        Application.Run(); // message loop until Dispose posts the stop message
        var remove = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevRemove, Target = IntPtr.Zero };
        RegisterRawInputDevices([remove], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        _sink.DestroyHandle();
    }

    // RAWINPUTHEADER is 24 bytes on x64; RAWMOUSE follows: usFlags(2) pad(2) usButtonFlags(2) usButtonData(2)
    // ulRawButtons(4) lLastX(4) lLastY(4)
    const int HeaderSize = 24, ButtonFlags = HeaderSize + 4, LastX = HeaderSize + 12, LastY = HeaderSize + 16;
    readonly byte[] _buffer = new byte[64];

    void OnInput(IntPtr handle)
    {
        long now = Stopwatch.GetTimestamp();
        uint size = (uint)_buffer.Length;
        if (GetRawInputData(handle, RidInput, _buffer, ref size, HeaderSize) == unchecked((uint)-1)) return;
        if (BitConverter.ToInt32(_buffer, 0) != 0) return; // RIM_TYPEMOUSE
        var hDevice = (IntPtr)BitConverter.ToInt64(_buffer, 8);
        ushort flags = BitConverter.ToUInt16(_buffer, ButtonFlags);
        bool moved = BitConverter.ToInt32(_buffer, LastX) != 0 || BitConverter.ToInt32(_buffer, LastY) != 0;
        if (!moved && flags == 0) return;

        Device d;
        lock (_gate)
            if (!_devices.TryGetValue(hDevice, out d!))
                _devices[hDevice] = d = new Device(Stopwatch.Frequency) { Name = DeviceName(hDevice), Clicks = { ThresholdMs = _thresholdMs } };
        d.LastEvent = now;
        if (moved) d.Polling.Add(now);
        for (int b = 0; b < 5; b++)
        {
            if ((flags & (1 << (2 * b))) != 0) d.Clicks.Down(b, now);
            if ((flags & (2 << (2 * b))) != 0) d.Clicks.Up(b, now);
        }
    }

    /// <summary>The product name of a raw-input device (its HID path → the USB parent's reported name).</summary>
    static string? DeviceName(IntPtr hDevice)
    {
        uint chars = 0;
        GetRawInputDeviceInfoW(hDevice, RidiDeviceName, IntPtr.Zero, ref chars);
        if (chars == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RidiDeviceName, buffer, ref chars) == unchecked((uint)-1)) return null;
            var path = Marshal.PtrToStringUni(buffer) ?? "";
            return DeviceTree.BusReportedName(Hub.Core.Diagnostics.DeviceKinds.InstanceIdFromInterfacePath(path));
        }
        catch (Exception) { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_sink is { } sink && sink.Handle != IntPtr.Zero) PostMessage(sink.Handle, WmStop, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }

    /// <summary>Message-only window receiving WM_INPUT.</summary>
    sealed class Sink : NativeWindow
    {
        readonly RawMouse _owner;
        public Sink(RawMouse owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /* HWND_MESSAGE */ });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmInput) { _owner.OnInput(m.LParam); }
            else if (m.Msg == WmStop) { Application.ExitThread(); return; }
            base.WndProc(ref m);
        }
    }

    const int WmInput = 0x00FF, WmStop = 0x0400 + 77;
    const uint RidevInputSink = 0x100, RidevRemove = 0x1, RidInput = 0x10000003, RidiDeviceName = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }

    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[] data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}
