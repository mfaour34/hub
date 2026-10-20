using Hub.Core.Devices;
using Hub.Core.Hid;
using Hub.Core.Logitech;
using Hub.Core.Razer;

namespace Hub.Tests;

/// <summary>The catalog keeps verified models on their tested paths; other models degrade to what they answer.</summary>
public class GenericDeviceTests
{
    [Fact]
    public void Viper_v3_pro_keeps_its_verified_protocol_details()
    {
        var wired = DeviceCatalog.KnownMouse(RazerMouse.WiredProductId)!;
        var dongle = DeviceCatalog.KnownMouse(RazerMouse.DongleProductId)!;

        Assert.Same(wired, dongle);
        Assert.Equal("Viper V3 Pro", wired.Name);
        Assert.Equal((byte)0x1F, wired.TransactionId);
        Assert.Equal(PollingScheme.HyperPolling, wired.DonglePolling);
        Assert.Equal(PollingScheme.Profile, wired.WiredPolling);
        Assert.Equal([1, 3, 5, 4, 2, 9, 10, 96], wired.Buttons.Select(b => (int)b.Id));
        Assert.Null(wired.Render); // no vendor artwork is shipped
    }

    [Fact]
    public void Catalog_options_produce_the_same_packets_as_the_original_defaults()
    {
        foreach (var dongle in new[] { true, false })
        {
            var model = DeviceCatalog.Mice[0];
            var a = new ScriptedChannel(); var b = new ScriptedChannel();
            new RazerMouse(a, dongle).SetPollingRate(1000);
            new RazerMouse(b, dongle, model.TransactionId!.Value, dongle ? model.DonglePolling : model.WiredPolling).SetPollingRate(1000);
            Assert.Equal(a.Sent.Single(), b.Sent.Single());
        }
    }

    [Fact]
    public void Pro_x_2_is_the_catalogued_headset()
    {
        var h = DeviceCatalog.KnownHeadset(0x0AF7)!;

        Assert.Equal("PRO X 2 LIGHTSPEED", h.Name);
        Assert.Empty(h.Colors);
        Assert.Null(DeviceCatalog.KnownHeadset(0x1234));
        Assert.False(DeviceCatalog.GenericHeadset(0x1234).Known);
    }

    [Fact]
    public void Unknown_mouse_is_identified_by_transaction_id_and_polling_scheme()
    {
        // an older mouse: answers only transaction id 0xFF and only the classic 00,85 polling command
        var channel = new ScriptedChannel(acceptTid: 0xFF, unsupported: [(0x00, 0xC0), (0x00, 0x8E)]);

        var id = RazerMouse.Identify(channel, viaDongle: false);

        Assert.Equal(((byte)0xFF, PollingScheme.Legacy), id);
    }

    [Fact]
    public void Legacy_polling_uses_00_05_with_a_single_code_byte()
    {
        var channel = new ScriptedChannel(acceptTid: 0x3F);
        new RazerMouse(channel, viaDongle: false, 0x3F, PollingScheme.Legacy).SetPollingRate(500);

        var p = channel.Sent.Single().AsSpan(1);
        Assert.Equal(0x3F, p[1]);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x05, 0x02 }, p.Slice(5, 4).ToArray());
    }

    [Fact]
    public void Probe_hides_what_the_mouse_does_not_support()
    {
        // a wired mouse without battery, rotation or dynamic sensitivity, and no button 5
        var channel = new ScriptedChannel(unsupported: [(0x07, 0x80), (0x07, 0x83), (0x0B, 0x94), (0x0B, 0x90)], unsupportedButtons: [5]);
        var mouse = new RazerMouse(channel, viaDongle: false, 0x1F, PollingScheme.Legacy);

        var caps = mouse.Probe(DeviceCatalog.StandardButtons.Select(b => b.Id));

        Assert.True(caps.DpiStages);
        Assert.True(caps.Polling);
        Assert.False(caps.Battery);
        Assert.False(caps.PowerSaving);
        Assert.False(caps.Rotation);
        Assert.False(caps.DynamicSensitivity);
        Assert.True(caps.LiftOff);
        Assert.DoesNotContain((byte)5, caps.Buttons);
        Assert.Contains((byte)4, caps.Buttons);
    }

    [Fact]
    public void Headset_eq_layout_is_read_from_the_device()
    {
        // fn1 reply read from a PRO X 2 LIGHTSPEED
        var f = OnboardEq.ParseFrequencies(Convert.FromHexString("800500" + "50fa0200f0fe0202eefc020898060219c80502"));

        Assert.Equal([80, 240, 750, 2200, 6600], f);
        Assert.True(OnboardEq.IsSupportedLayout(f));
        Assert.False(OnboardEq.IsSupportedLayout([100, 1000, 10000]));
    }

    /// <summary>
    /// A Razer mouse that answers only <paramref name="acceptTid"/> (others get Failure), reports NotSupported for
    /// the listed commands, and answers firmware-like defaults for the rest.
    /// </summary>
    sealed class ScriptedChannel(byte acceptTid = 0x1F, (byte Cls, byte Id)[]? unsupported = null, byte[]? unsupportedButtons = null) : IFeatureChannel
    {
        public List<byte[]> Sent { get; } = [];

        public void SetFeature(byte[] report) => Sent.Add(report);

        public byte[] GetFeature(int length)
        {
            var resp = (byte[])Sent[^1].Clone();
            byte cls = resp[7], id = resp[8];
            var status = resp[2] != acceptTid ? RazerStatus.Failure
                : unsupported?.Contains((cls, id)) == true ? RazerStatus.NotSupported
                : cls == 0x02 && id == 0x8C && unsupportedButtons?.Contains(resp[10]) == true ? RazerStatus.NotSupported
                : RazerStatus.Success;
            resp[1] = (byte)status;
            // polling replies: code 1 (1000 Hz) in the byte each scheme reads
            if (cls == 0x00 && id is 0x85) resp[9] = 1;
            if (cls == 0x00 && id is 0xC0 or 0x8E) resp[10] = 1;
            return resp;
        }

        public void Dispose() { }
    }
}
