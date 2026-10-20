namespace Hub.Core.Hid;

/// <summary>Feature-report transport (Razer). Reports include the leading report-ID byte.</summary>
public interface IFeatureChannel : IDisposable
{
    void SetFeature(byte[] report);
    byte[] GetFeature(int length);
}

/// <summary>Interrupt-report transport (Logitech Centurion). Frames include the leading report-ID byte.</summary>
public interface IReportChannel : IDisposable
{
    void Write(byte[] frame);

    /// <summary>Raised on a background thread for every input report.</summary>
    event Action<byte[]>? FrameReceived;

    /// <summary>Raised when the underlying device handle goes away (unplugged dongle).</summary>
    event Action? Closed;
}
