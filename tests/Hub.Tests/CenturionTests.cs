using Hub.Core.Hid;
using Hub.Core.Logitech;
using static Hub.Tests.RazerTests;

namespace Hub.Tests;

public class CenturionTests
{
    [Fact]
    public void Eq_payload_matches_bytes_the_headset_accepted()
    {
        // gains [-1,-1,4,3,2], slot 0, Q 2 — first 40 of 253 bytes, as accepted by a PRO X 2
        var payload = OnboardEq.BuildSetPayload([-1, -1, 4, 3, 2], save: false);

        Assert.Equal(253, payload.Length);
        Assert.Equal(Hex("00 05 00 50 ff 02 00 f0 ff 02 02 ee 04 02 08 98 03 02 19 c8 02 02 05 5a e3 00 03 0e 00 02 00 00 00 01 00 35 00 05 00 a4"),
            payload[..40]);
    }

    [Fact]
    public void Save_flag_sets_slot_byte_0x80()
    {
        Assert.Equal(0x80, OnboardEq.BuildSetPayload([-6, -2, -4, 6, 5], save: true)[0]);
    }

    [Fact]
    public void Parses_eq_read_from_the_headset()
    {
        var gains = OnboardEq.ParseGains(Hex("80 05 00 50 fa 02 00 f0 fe 02 02 ee fc 02 08 98 06 02 19 c8 05 02"));

        Assert.Equal([-6, -2, -4, 6, 5], gains);
    }

    [Fact]
    public void Eq_write_is_split_into_five_bridge_fragments()
    {
        var frames = CenturionFrames.Bridged(3, 9, 2, 7, OnboardEq.BuildSetPayload([0, 0, 0, 0, 0], false));

        Assert.Equal(5, frames.Count);
        Assert.Equal(new byte[] { 0x51, 61, 0x01, 0x03, 0x17, 0x01, 0x00 }, frames[0][..7]); // 256-byte sub-message
        Assert.Equal(new byte[] { 0x02 | 1, 0x04 | 1, 0x06 | 1, 0x08 }, frames.Skip(1).Select(f => f[2]).ToArray());
    }

    [Fact]
    public async Task Reads_battery_and_raises_battery_events()
    {
        var dongle = new FakeDongle();
        using var headset = new CenturionHeadset(dongle);
        HeadsetBattery? evt = null;
        headset.BatteryChanged += b => evt = b;

        await headset.InitializeAsync();
        var battery = await headset.GetBatteryAsync();
        dongle.Inject(Hex("51 0b 00 03 10 00 06 00 04 00 48 48 00")); // event captured from a PRO X 2

        Assert.True(headset.HeadsetConnected);
        Assert.Equal(new HeadsetBattery(73, false), battery);
        Assert.Equal(new HeadsetBattery(72, false), evt);
    }

    [Fact]
    public async Task Headset_error_frame_becomes_exception()
    {
        var dongle = new FakeDongle();
        using var headset = new CenturionHeadset(dongle);
        await headset.InitializeAsync();

        await Assert.ThrowsAsync<CenturionException>(() => headset.GetFirmwareAsync());
    }

    /// <summary>
    /// Plays the dongle side using replies recorded from a PRO X 2 (its feature map).
    /// DeviceInfo answers with a headset error to exercise the error path.
    /// </summary>
    sealed class FakeDongle : IReportChannel
    {
        static readonly Dictionary<ushort, byte> HeadsetFeatures = new() { [0x0104] = 4, [0x0100] = 3 };

        public event Action<byte[]>? FrameReceived;
        public event Action? Closed;

        public void Inject(byte[] frame) => FrameReceived?.Invoke(frame);

        public void Write(byte[] frame)
        {
            var p = CenturionFrames.Payload(frame)!;
            if (p[0] == 0x00) // Root.getFeature(0x0003) on the dongle
                Reply([0x00, p[1], 0x03, 0x00, 0x01]);
            else if (p[0] == 0x03 && p[1] >> 4 == 0) // bridge GetConnectionInfo
                Reply([0x03, p[1], 0x03, 0x7a, 0x00, 0x01, 0x01]);
            else if (p[0] == 0x03) // bridged: [3][0x1s][len][len][00][sub][fn|sw][params]
            {
                byte sub = p[5], fnSw = p[6];
                Reply([0x03, p[1]]); // ACK
                if (sub == 0x00)
                {
                    ushort id = (ushort)((p[7] << 8) | p[8]);
                    HeadsetMessage(sub, fnSw, [HeadsetFeatures.GetValueOrDefault(id), 0x00, 0x01]);
                }
                else if (sub == 4)
                    HeadsetMessage(sub, fnSw, [0x49, 0x49, 0x00]);
                else
                    Reply([0x03, 0x10, 0x00, 0x05, 0x00, 0xFF, sub, fnSw, 0x02]);
            }
        }

        void HeadsetMessage(byte sub, byte fnSw, byte[] data) =>
            Reply([0x03, 0x10, 0x00, (byte)(3 + data.Length), 0x00, sub, fnSw, .. data]);

        void Reply(byte[] payload) => FrameReceived?.Invoke(CenturionFrames.Frame(payload));

        public void Dispose() => Closed?.Invoke();
    }
}
