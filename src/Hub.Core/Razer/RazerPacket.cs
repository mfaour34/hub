namespace Hub.Core.Razer;

/// <summary>
/// The 90-byte Razer control packet (docs/protocols.md), sent as a 91-byte feature report with report ID 0.
/// </summary>
public static class RazerPacket
{
    public const int PacketLength = 90;
    public const int ReportLength = PacketLength + 1;
    public const byte DefaultTransactionId = 0x1F;

    public static byte[] BuildReport(byte commandClass, byte commandId, byte dataSize, ReadOnlySpan<byte> args,
        byte transactionId = DefaultTransactionId)
    {
        if (args.Length > 80)
            throw new ArgumentException("Razer packets carry at most 80 argument bytes", nameof(args));

        var report = new byte[ReportLength];
        var p = report.AsSpan(1);
        p[1] = transactionId;
        p[5] = dataSize;
        p[6] = commandClass;
        p[7] = commandId;
        args.CopyTo(p[8..]);
        p[88] = Checksum(p);
        return report;
    }

    /// <summary>XOR of packet bytes 2..87.</summary>
    public static byte Checksum(ReadOnlySpan<byte> packet)
    {
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= packet[i];
        return crc;
    }
}

public enum RazerStatus : byte
{
    New = 0,
    Busy = 1,
    Success = 2,
    Failure = 3,
    Timeout = 4,
    NotSupported = 5,
}

public sealed class RazerException(string message, RazerStatus? status = null) : Exception(message)
{
    public RazerStatus? Status { get; } = status;
}
