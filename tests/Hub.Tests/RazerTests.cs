using Hub.Core.Hid;
using Hub.Core.Razer;

namespace Hub.Tests;

public class RazerTests
{
    [Fact]
    public void Checksum_matches_a_captured_packet()
    {
        // captured "set indicator LED" packet: 00 15 00 00 00 01 07 10 02 … CRC 0x14
        var report = RazerPacket.BuildReport(0x07, 0x10, 0x01, [0x02], transactionId: 0x15);

        Assert.Equal(91, report.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x15, 0x00, 0x00, 0x00, 0x01, 0x07, 0x10, 0x02 }, report[..10]);
        Assert.Equal(0x14, report[89]);
    }

    [Fact]
    public void Parses_dpi_stages_read_from_the_mouse()
    {
        // 04,86 reply read from a Viper V3 Pro
        var mouse = new RazerMouse(new FakeFeatureChannel(Hex("01 01 02 01 03 20 03 20 00 00 02 01 90 01 90 00 00")), viaDongle: false);

        var stages = mouse.GetDpiStages();

        Assert.Equal(1, stages.ActiveStage);
        Assert.Equal([new DpiStage(800, 800), new DpiStage(400, 400)], stages.Stages);
    }

    [Fact]
    public void Dpi_stage_write_uses_zero_based_stage_ids_and_7n_plus_3_size()
    {
        var channel = new FakeFeatureChannel([]);
        var mouse = new RazerMouse(channel, viaDongle: false);

        mouse.SetDpiStages(new DpiStages(1, [new DpiStage(800, 800), new DpiStage(400, 400)]));

        var p = channel.Sent.Single().AsSpan(1);
        Assert.Equal(17, p[5]);
        Assert.Equal(0x04, p[6]);
        Assert.Equal(0x06, p[7]);
        Assert.Equal(Hex("01 01 02 00 03 20 03 20 00 00 01 01 90 01 90 00 00"), p.Slice(8, 17).ToArray());
    }

    [Fact]
    public void Battery_converts_raw_255_scale_to_percent()
    {
        var mouse = new RazerMouse(new FakeFeatureChannel(Hex("00 36")), viaDongle: true);

        Assert.Equal(21, mouse.GetBattery().Percent);
    }

    [Fact]
    public void Hyperpolling_over_dongle_uses_00_40()
    {
        var channel = new FakeFeatureChannel([]);
        new RazerMouse(channel, viaDongle: true).SetPollingRate(8000);

        var p = channel.Sent.Single().AsSpan(1);
        Assert.Equal(new byte[] { 0x00, 0x40, 0x01, 0x01 }, new[] { p[6], p[7], p[8], p[9] });
    }

    [Fact]
    public void Not_supported_status_throws()
    {
        var mouse = new RazerMouse(new FakeFeatureChannel([], RazerStatus.NotSupported), viaDongle: true);

        var ex = Assert.Throws<RazerException>(() => mouse.GetPollingRate());
        Assert.Equal(RazerStatus.NotSupported, ex.Status);
    }

    internal static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    /// <summary>Answers every request with the given data, echoing transaction id, class and id like the firmware.</summary>
    sealed class FakeFeatureChannel(byte[] data, RazerStatus status = RazerStatus.Success) : IFeatureChannel
    {
        public List<byte[]> Sent { get; } = [];

        public void SetFeature(byte[] report) => Sent.Add(report);

        public byte[] GetFeature(int length)
        {
            var resp = (byte[])Sent[^1].Clone();
            resp[1] = (byte)status;
            Array.Clear(resp, 9, 80);
            data.CopyTo(resp, 9);
            return resp;
        }

        public void Dispose() { }
    }
}
