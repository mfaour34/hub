using System.Globalization;
using System.Text.RegularExpressions;

namespace Hub.Core.Diagnostics;

/// <summary>Turns the Windows event records Hub subscribes to into crash-log entries; everything else is ignored.</summary>
public static class EventClassifier
{
    public const string Display = "Display";
    public const string Whea = "Microsoft-Windows-WHEA-Logger";
    public const string BugCheck = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string KernelPower = "Microsoft-Windows-Kernel-Power";
    public const string Wer = "Windows Error Reporting";

    public static StabilityEvent? Classify(string log, string provider, int eventId, long recordId, DateTimeOffset time,
        IReadOnlyDictionary<string, string> data)
    {
        StabilityEvent Make(string kind, string title, string detail = "", string? key = null) =>
            new(key ?? $"{log}:{recordId}", time, kind, title, detail, log, recordId);

        if (log == "System")
            return (provider, eventId) switch
            {
                (Display, 4101) => Make("gpu-reset", "GPU driver stopped responding and recovered"),
                // corrected errors can come in storms (hundreds an hour from a PCIe link): one entry per source and hour
                (Whea, 17 or 19 or 47) => Make("hardware-error", WheaTitle(eventId), WheaDetail(data),
                    key: $"whea:{eventId}:{data.GetValueOrDefault("ErrorSource")}:{time.UtcDateTime:yyyyMMddHH}"),
                (Whea, _) => Make("hardware-error", WheaTitle(eventId), WheaDetail(data)),
                (BugCheck, 1001) => Make("bluescreen", "Blue screen", data.GetValueOrDefault("param1") ?? ""),
                // after a blue screen Kernel-Power 41 carries its bug check; the blue screen entry already covers it
                (KernelPower, 41) when BugcheckCode(data) == 0 => Make("unexpected-restart", "Restarted without a clean shutdown", "power loss or hard freeze"),
                _ => null,
            };

        // Live kernel reports: the GPU driver was reset (141/117) or the hardware reported an error (124) without a
        // blue screen. WER re-posts queued reports for weeks, under new report ids, so the dump file it attaches
        // (named after the moment it was taken) is both the identity and the real time; the report id is the fallback.
        if (log == "Application" && provider == Wer && eventId == 1001 && data.GetValueOrDefault("EventName") == "LiveKernelEvent"
            && data.GetValueOrDefault("ReportId") is { Length: > 0 } reportId)
        {
            var code = data.GetValueOrDefault("P1") ?? "";
            var key = $"wer:{reportId}";
            if (DumpName().Match(data.GetValueOrDefault("AttachedFiles") ?? "") is { Success: true } dump
                && DateTime.TryParseExact(dump.Groups[1].Value + dump.Groups[2].Value, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken))
            {
                key = $"dump:{dump.Value}";
                time = new DateTimeOffset(taken, TimeZoneInfo.Local.GetUtcOffset(taken));
            }
            return code switch
            {
                "141" or "117" => Make("gpu-reset", $"GPU reset (LiveKernelEvent {code})", key: key),
                "124" => Make("hardware-error", "Hardware error (LiveKernelEvent 124)", key: key),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>LiveKernelReports dump names: WATCHDOG-20261020-1859.dmp, WHEA-20261019-2236.dmp (local time).</summary>
    static Regex DumpName() => DumpRegex;
    static readonly Regex DumpRegex = new(@"[A-Z_]+-(\d{8})-(\d{4})(?:-\d+)?\.dmp", RegexOptions.Compiled);

    static string WheaTitle(int id) => id switch
    {
        18 => "Fatal hardware error (machine check)",
        17 or 19 or 47 => "Corrected hardware error",
        _ => "Hardware error",
    };

    /// <summary>WHEA_ERROR_SOURCE_TYPE values that show up on desktops.</summary>
    static readonly Dictionary<string, string> ErrorSources = new()
    {
        ["0"] = "machine check (MCE)", ["1"] = "corrected machine check", ["2"] = "corrected platform error",
        ["3"] = "machine check", ["4"] = "NMI", ["6"] = "PCI Express", ["7"] = "generic", ["8"] = "IA-32 init",
    };

    static string WheaDetail(IReadOnlyDictionary<string, string> data)
    {
        var parts = new List<string>();
        if (data.GetValueOrDefault("ErrorSource") is { } src) parts.Add("source: " + ErrorSources.GetValueOrDefault(src, src));
        // "ApicId" on current Windows, "ProcessorAPICID" on older builds
        if ((data.GetValueOrDefault("ApicId") ?? data.GetValueOrDefault("ProcessorAPICID")) is { } apic) parts.Add($"CPU APIC {apic}");
        if (data.GetValueOrDefault("MCABank") is { } bank) parts.Add($"bank {bank}");
        return string.Join(" · ", parts);
    }

    static long BugcheckCode(IReadOnlyDictionary<string, string> data) =>
        long.TryParse(data.GetValueOrDefault("BugcheckCode"), out var code) ? code : 0;
}
