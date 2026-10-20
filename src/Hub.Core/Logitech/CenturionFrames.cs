namespace Hub.Core.Logitech;

/// <summary>
/// Centurion transport framing (docs/protocols.md).
/// Frame: [0x51][LEN = payload+1][flags: fragIdx&lt;&lt;1 | more][payload], zero-padded to 64 bytes.
/// </summary>
public static class CenturionFrames
{
    public const byte ReportId = 0x51;
    public const int FrameLength = 64;
    public const int FirstFragmentMax = 56;
    public const int ContinuationMax = 60;

    public static byte[] Frame(ReadOnlySpan<byte> payload, byte flags = 0)
    {
        if (payload.Length > FrameLength - 3) throw new ArgumentException("payload too large for one frame");
        var f = new byte[FrameLength];
        f[0] = ReportId;
        f[1] = (byte)(payload.Length + 1);
        f[2] = flags;
        payload.CopyTo(f.AsSpan(3));
        return f;
    }

    /// <summary>Payload of an input frame (bytes after LEN and flags), or null if it is not a Centurion frame.</summary>
    public static byte[]? Payload(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4 || frame[0] != ReportId) return null;
        int len = frame[1];
        if (len <= 1 || len + 2 > frame.Length) return null;
        return frame.Slice(3, len - 1).ToArray();
    }

    public static byte Flags(ReadOnlySpan<byte> frame) => frame.Length > 2 ? frame[2] : (byte)0;

    /// <summary>Direct request to the dongle: [featIdx][fn&lt;&lt;4 | swid][params].</summary>
    public static byte[] Direct(byte featureIndex, byte function, byte softwareId, ReadOnlySpan<byte> parameters)
    {
        var p = new byte[2 + parameters.Length];
        p[0] = featureIndex;
        p[1] = (byte)((function << 4) | softwareId);
        parameters.CopyTo(p.AsSpan(2));
        return Frame(p);
    }

    /// <summary>
    /// Bridged request to the headset through dongle feature 0x0003, fragmented as needed.
    /// Sub-message: [0x00][subFeatIdx][fn&lt;&lt;4 | swid][params]; first fragment prefixed by [bridgeIdx][0x10|swid][lenHi][lenLo].
    /// </summary>
    public static List<byte[]> Bridged(byte bridgeIndex, byte subFeatureIndex, byte function, byte softwareId,
        ReadOnlySpan<byte> parameters)
    {
        int size = 3 + parameters.Length;
        if (size > 0x0FFF) throw new ArgumentException("bridged message exceeds 12-bit length");
        var msg = new byte[size];
        msg[1] = subFeatureIndex;
        msg[2] = (byte)((function << 4) | softwareId);
        parameters.CopyTo(msg.AsSpan(3));

        var frames = new List<byte[]>();
        int offset = 0, fragment = 0;
        while (offset < size)
        {
            int chunk = Math.Min(fragment == 0 ? FirstFragmentMax : ContinuationMax, size - offset);
            bool more = offset + chunk < size;
            var payload = new List<byte>(4 + chunk);
            if (fragment == 0)
                payload.AddRange([bridgeIndex, (byte)(0x10 | softwareId), (byte)((size >> 8) & 0x0F), (byte)size]);
            payload.AddRange(msg.AsSpan(offset, chunk).ToArray());
            frames.Add(Frame(payload.ToArray(), (byte)((fragment << 1) | (more ? 1 : 0))));
            offset += chunk;
            fragment++;
        }
        return frames;
    }
}
