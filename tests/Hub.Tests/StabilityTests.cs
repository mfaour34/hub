using Hub.Core.Diagnostics;

namespace Hub.Tests;

public class StabilityTests
{
    static readonly DateTimeOffset T = new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);
    static Dictionary<string, string> D(params (string, string)[] kv) => kv.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Display_4101_is_a_gpu_reset()
    {
        var e = EventClassifier.Classify("System", "Display", 4101, 7, T, D())!;
        Assert.Equal("gpu-reset", e.Kind);
        Assert.Equal("System:7", e.Key);
    }

    [Theory]
    [InlineData("ApicId")]           // current Windows 11
    [InlineData("ProcessorAPICID")]  // older builds
    public void Whea_18_is_fatal_with_details(string apicField)
    {
        var e = EventClassifier.Classify("System", "Microsoft-Windows-WHEA-Logger", 18, 1, T,
            D(("ErrorSource", "3"), (apicField, "4"), ("MCABank", "5")))!;
        Assert.Equal("hardware-error", e.Kind);
        Assert.Equal("Fatal hardware error (machine check)", e.Title);
        Assert.Contains("machine check", e.Detail);
        Assert.Contains("APIC 4", e.Detail);
        Assert.Contains("bank 5", e.Detail);
    }

    [Fact]
    public void Kernel_power_41_without_bugcheck_is_an_unexpected_restart()
    {
        var e = EventClassifier.Classify("System", "Microsoft-Windows-Kernel-Power", 41, 1, T, D(("BugcheckCode", "0")))!;
        Assert.Equal("unexpected-restart", e.Kind);
        Assert.Equal("power loss or hard freeze", e.Detail);
    }

    [Fact]
    public void Kernel_power_41_after_a_blue_screen_is_left_to_the_blue_screen_entry() =>
        Assert.Null(EventClassifier.Classify("System", "Microsoft-Windows-Kernel-Power", 41, 1, T, D(("BugcheckCode", "278"))));

    [Fact]
    public void Corrected_whea_errors_share_one_entry_per_source_and_hour()
    {
        var a = EventClassifier.Classify("System", "Microsoft-Windows-WHEA-Logger", 17, 1, T.AddMinutes(1), D(("ErrorSource", "6")))!;
        var b = EventClassifier.Classify("System", "Microsoft-Windows-WHEA-Logger", 17, 2, T.AddMinutes(40), D(("ErrorSource", "6")))!;
        var c = EventClassifier.Classify("System", "Microsoft-Windows-WHEA-Logger", 17, 3, T.AddMinutes(70), D(("ErrorSource", "6")))!;
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
        var fatal = EventClassifier.Classify("System", "Microsoft-Windows-WHEA-Logger", 18, 4, T.AddMinutes(2), D(("ErrorSource", "3")))!;
        Assert.Equal("System:4", fatal.Key); // fatal ones stay individual
    }

    [Fact]
    public void Bluescreen_carries_the_bugcheck_text()
    {
        var e = EventClassifier.Classify("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, 1, T, D(("param1", "0x00000116 (0x1, 0x2)")))!;
        Assert.Equal("bluescreen", e.Kind);
        Assert.Equal("0x00000116 (0x1, 0x2)", e.Detail);
    }

    [Fact]
    public void Live_kernel_141_keys_on_report_id()
    {
        var data = D(("EventName", "LiveKernelEvent"), ("P1", "141"), ("ReportId", "4ea23405"));
        var a = EventClassifier.Classify("Application", "Windows Error Reporting", 1001, 100, T, data)!;
        var b = EventClassifier.Classify("Application", "Windows Error Reporting", 1001, 200, T.AddHours(1), data)!;
        Assert.Equal("gpu-reset", a.Kind);
        Assert.Equal(a.Key, b.Key);
        Assert.Equal("wer:4ea23405", a.Key);
    }

    [Fact]
    public void Live_kernel_time_and_key_come_from_the_dump_file()
    {
        // WER re-posts old dumps for weeks (dumps from months earlier), under new report ids
        const string files = @"\\?\C:\WINDOWS\LiveKernelReports\WATCHDOG\WATCHDOG-20261020-1859.dmp
\\?\C:\WINDOWS\SystemTemp\WER-2439750-0.sysdata.xml";
        var a = EventClassifier.Classify("Application", "Windows Error Reporting", 1001, 1, T,
            D(("EventName", "LiveKernelEvent"), ("P1", "141"), ("ReportId", "r1"), ("AttachedFiles", files)))!;
        var b = EventClassifier.Classify("Application", "Windows Error Reporting", 1001, 2, T.AddDays(1),
            D(("EventName", "LiveKernelEvent"), ("P1", "141"), ("ReportId", "r2"), ("AttachedFiles", files)))!;
        Assert.Equal(a.Key, b.Key);
        Assert.Equal("dump:WATCHDOG-20261020-1859.dmp", a.Key);
        var local = new DateTime(2026, 10, 20, 18, 59, 0);
        Assert.Equal(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)), a.Time);
    }

    [Theory]
    [InlineData("a1000001")]
    [InlineData("a2000002")]
    [InlineData("193")]
    public void Live_kernel_noise_codes_are_ignored(string code) =>
        Assert.Null(EventClassifier.Classify("Application", "Windows Error Reporting", 1001, 1, T,
            D(("EventName", "LiveKernelEvent"), ("P1", code), ("ReportId", "x"))));

    [Fact]
    public void Other_wer_events_are_ignored() =>
        Assert.Null(EventClassifier.Classify("Application", "Windows Error Reporting", 1001, 1, T, D(("EventName", "APPCRASH"))));

    [Fact]
    public void EventLog_6008_is_ignored() =>
        Assert.Null(EventClassifier.Classify("System", "EventLog", 6008, 1, T, D()));
}

public sealed class CrashLogTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    readonly string _path = Path.Combine(Path.GetTempPath(), $"hub-crashlog-{Guid.NewGuid():N}.jsonl");

    static StabilityEvent E(string key, DateTimeOffset time) => new(key, time, "gpu-reset", "GPU reset", "", "System", 1);

    [Fact]
    public void Add_dedupes_by_key()
    {
        var log = new CrashLog(_path);
        Assert.Equal(1, log.Add([E("a", Now.AddHours(-1)), E("a", Now.AddHours(-1))], Now));
        Assert.Equal(0, log.Add([E("a", Now.AddMinutes(-5))], Now));
        Assert.Single(log.Recent(Now));
    }

    [Fact]
    public void Old_events_are_pruned()
    {
        var log = new CrashLog(_path);
        log.Add([E("old", Now.AddDays(-8)), E("kept", Now.AddDays(-6))], Now);
        Assert.Equal(["kept"], log.Recent(Now).Select(e => e.Key));
        Assert.Empty(log.Recent(Now.AddDays(2)));
    }

    [Fact]
    public void Survives_reload()
    {
        new CrashLog(_path).Add([E("a", Now.AddHours(-2)), E("b", Now.AddHours(-1))], Now);
        var reloaded = new CrashLog(_path);
        Assert.Equal(2, reloaded.Recent(Now).Count);
        Assert.Equal(Now.AddHours(-1), reloaded.Newest);
    }

    [Fact]
    public void Corrupt_lines_are_skipped()
    {
        var log = new CrashLog(_path);
        log.Add([E("a", Now.AddHours(-2))], Now);
        var good = File.ReadAllLines(_path)[0];
        File.WriteAllLines(_path, [good, "{\"Key\":", good.Replace("\"a\"", "\"b\"")]);
        Assert.Equal(2, new CrashLog(_path).Recent(Now).Count);
    }

    [Fact]
    public void The_same_incident_from_two_logs_is_one_entry()
    {
        // a machine check writes WHEA 18 (System) and a WHEA live dump (WER) in the same minute
        var log = new CrashLog(_path);
        var whea = new StabilityEvent("System:136265", Now.AddHours(-1), "hardware-error", "Fatal hardware error (machine check)", "", "System", 136265);
        var dump = new StabilityEvent("dump:WHEA-20261019-2236.dmp", Now.AddHours(-1).AddSeconds(40), "hardware-error", "Hardware error (LiveKernelEvent 124)", "", "Application", 9);
        Assert.Equal(1, log.Add([whea, dump], Now));
        Assert.Equal("System:136265", Assert.Single(log.Recent(Now)).Key);
    }

    [Fact]
    public void Separate_gpu_resets_minutes_apart_both_count()
    {
        var log = new CrashLog(_path);
        log.Add([new("dump:WATCHDOG-1857.dmp", Now.AddMinutes(-2), "gpu-reset", "GPU reset", "", "Application", 1),
                 new("dump:WATCHDOG-1859.dmp", Now, "gpu-reset", "GPU reset", "", "Application", 2)], Now);
        Assert.Equal(2, log.Recent(Now).Count);
    }

    [Fact]
    public void A_write_failure_keeps_the_entry_in_memory()
    {
        var log = new CrashLog(_path);
        log.Add([E("a", Now.AddHours(-1))], Now);
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None)) // a scanner holding the file
            Assert.Equal(1, log.Add([E("b", Now.AddMinutes(-1))], Now));
        Assert.Equal(2, log.Recent(Now).Count);
    }

    [Fact]
    public void Recent_is_newest_first()
    {
        var log = new CrashLog(_path);
        log.Add([E("a", Now.AddHours(-3)), E("b", Now.AddHours(-1)), E("c", Now.AddHours(-2))], Now);
        Assert.Equal(["b", "c", "a"], log.Recent(Now).Select(e => e.Key));
    }

    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }
}

public class SpecsTextTests
{
    [Fact]
    public void SpecsText_formats_sections_and_skips_empty() =>
        Assert.Equal("cpu\n  model: Example 8-core CPU\n",
            SpecsText.Format([new SpecSection("cpu", [new SpecRow("model", "Example 8-core CPU")]), new SpecSection("network", [])]));

    [Fact]
    public void SpecsText_separates_sections_with_a_blank_line() =>
        Assert.Equal("cpu\n  cores: 8\n\ngpu\n  model: Example GPU\n",
            SpecsText.Format([new SpecSection("cpu", [new SpecRow("cores", "8")]), new SpecSection("gpu", [new SpecRow("model", "Example GPU")])]));
}
