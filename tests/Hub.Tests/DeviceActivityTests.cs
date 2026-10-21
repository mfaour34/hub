using Hub.Core.Diagnostics;

namespace Hub.Tests;

public sealed class DeviceActivityTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);
    readonly string _path = Path.Combine(Path.GetTempPath(), $"hub-devices-{Guid.NewGuid():N}.jsonl");

    static DeviceEvent E(string action, DateTimeOffset time, string id = @"USB\VID_31E3&PID_1322\A02B") =>
        new(time, action, "Acme TKL", "keyboard", id);

    [Fact]
    public void Quick_unplug_and_replug_is_one_reconnect()
    {
        var log = new DeviceActivityLog(_path);
        log.Add(E("disconnected", Now), Now);
        log.Add(E("connected", Now.AddSeconds(3)), Now.AddSeconds(3));
        var entry = Assert.Single(log.Recent(Now.AddSeconds(3)));
        Assert.Equal("reconnected", entry.Action);
        Assert.Equal(Now.AddSeconds(3), entry.Time);
    }

    [Fact]
    public void Slow_replug_is_two_entries()
    {
        var log = new DeviceActivityLog(_path);
        log.Add(E("disconnected", Now), Now);
        log.Add(E("connected", Now.AddSeconds(30)), Now.AddSeconds(30));
        Assert.Equal(["connected", "disconnected"], log.Recent(Now.AddSeconds(30)).Select(e => e.Action));
    }

    [Fact]
    public void Another_device_reconnecting_does_not_fold()
    {
        var log = new DeviceActivityLog(_path);
        log.Add(E("disconnected", Now, id: "A"), Now);
        log.Add(E("connected", Now.AddSeconds(2), id: "B"), Now.AddSeconds(2));
        Assert.Equal(2, log.Recent(Now.AddSeconds(2)).Count);
    }

    [Fact]
    public void Keeps_seven_days_and_survives_reload()
    {
        var log = new DeviceActivityLog(_path);
        log.Add(E("connected", Now.AddDays(-8)), Now);
        log.Add(E("connected", Now.AddDays(-1), id: "B"), Now);
        Assert.Single(new DeviceActivityLog(_path).Recent(Now));
    }

    [Theory]
    [InlineData(new[] { "HIDClass", "Keyboard", "Mouse" }, "keyboard")]
    [InlineData(new[] { "HIDClass", "Mouse" }, "mouse")]
    [InlineData(new[] { "MEDIA", "AudioEndpoint", "HIDClass" }, "audio")]
    [InlineData(new[] { "Camera", "MEDIA" }, "camera")]
    [InlineData(new[] { "DiskDrive", "Volume" }, "storage")]
    [InlineData(new[] { "XnaComposite" }, "controller")]
    [InlineData(new[] { "Bluetooth" }, "bluetooth")]
    [InlineData(new[] { "USB" }, "device")]
    [InlineData(new[] { "Bluetooth", "MEDIA", "AudioEndpoint" }, "bluetooth")]   // a Bluetooth adapter with audio children: its own class wins
    [InlineData(new[] { "USB", "HIDClass", "GameController" }, "controller")]  // a wireless controller receiver
    public void Kind_follows_the_most_telling_class(string[] classes, string expected) =>
        Assert.Equal(expected, DeviceKinds.FromClasses(classes));

    [Theory]
    [InlineData(@"\\?\USB#VID_31E3&PID_1322#A02B2452#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", @"USB\VID_31E3&PID_1322\A02B2452")]
    [InlineData(@"\\?\usb#vid_046d&pid_0aaf#5&1a2b&0&3#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", @"USB\VID_046D&PID_0AAF\5&1A2B&0&3")]
    public void Interface_path_maps_to_instance_id(string path, string expected) =>
        Assert.Equal(expected, DeviceKinds.InstanceIdFromInterfacePath(path));

    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }
}
