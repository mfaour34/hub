using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Hub.Core.Diagnostics;

namespace Hub.App.SystemInfo;

/// <summary>
/// Keeps the crash log: Windows pushes matching event records to Hub as they are written (no polling), and at
/// start-up Hub reads back what it missed while it wasn't running (blue screens and power loss are only logged at
/// the next boot). The queries select just the record types the classifier knows, so neither reads much.
/// </summary>
public sealed class StabilityWatcher(string logPath) : IDisposable
{
    const string SystemQuery =
        "(Provider[@Name='Display'] and EventID=4101)"
        + " or Provider[@Name='Microsoft-Windows-WHEA-Logger']"
        + " or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)"
        + " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)";

    // WER re-posts every queued report on each retry; filtering on the event's own data keeps that to GPU/WHEA reports
    const string WerQuery = "Provider[@Name='Windows Error Reporting'] and EventID=1001";
    const string WerData = "EventData[Data[@Name='EventName']='LiveKernelEvent' and (Data[@Name='P1']='141' or Data[@Name='P1']='117' or Data[@Name='P1']='124')]";

    readonly List<EventLogWatcher> _watchers = [];

    public CrashLog Log { get; } = new(logPath);

    /// <summary>Raised (on a background thread) when new entries were added.</summary>
    public event Action? Changed;

    public void Start()
    {
        Task.Run(() =>
        {
            try { CatchUp(); } catch (Exception) { /* event log service unavailable */ }
            Subscribe("System", $"*[System[{SystemQuery}]]");
            Subscribe("Application", $"*[System[{WerQuery}] and {WerData}]");
        });
    }

    /// <summary>The start-up catch-up alone, synchronously (`--systemtest`).</summary>
    public void CatchUpNow() => CatchUp();

    void CatchUp()
    {
        var since = (Log.Newest ?? DateTimeOffset.Now - CrashLog.Window).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var time = $"TimeCreated[@SystemTime>='{since}']";
        var found = Read("System", $"*[System[({SystemQuery}) and {time}]]")
            .Concat(Read("Application", $"*[System[{WerQuery} and {time}] and {WerData}]"));
        if (Log.Add(found, DateTimeOffset.Now) > 0) Changed?.Invoke();
    }

    static IEnumerable<StabilityEvent> Read(string log, string query)
    {
        var results = new List<StabilityEvent>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query));
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
                using (record)
                    if (Classify(log, record) is { } e) results.Add(e);
        }
        catch (EventLogException) { }
        return results;
    }

    void Subscribe(string log, string query)
    {
        try
        {
            var watcher = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, query));
            watcher.EventRecordWritten += (_, e) =>
            {
                // runs on a thread-pool callback with no handler above it: an exception here would end Hub
                try
                {
                    if (e.EventRecord is not { } record) return;
                    using (record)
                        if (Classify(log, record) is { } entry && Log.Add([entry], DateTimeOffset.Now) > 0) Changed?.Invoke();
                }
                catch (Exception) { /* record unreadable; the next start's catch-up reads it again */ }
            };
            watcher.Enabled = true;
            lock (_watchers) _watchers.Add(watcher);
        }
        catch (Exception) { /* log missing or access denied: the page shows what the catch-up found */ }
    }

    /// <summary>Named event data (EventRecord.Properties has the values but not their names).</summary>
    static StabilityEvent? Classify(string log, EventRecord record)
    {
        var data = new Dictionary<string, string>();
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            foreach (var d in XElement.Parse(record.ToXml()).Descendants(ns + "Data"))
                if (d.Attribute("Name")?.Value is { } name) data[name] = d.Value;
        }
        catch (Exception) { }
        var time = record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now;
        return EventClassifier.Classify(log, record.ProviderName ?? "", record.Id, record.RecordId ?? 0, time, data);
    }

    public void Dispose()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) { w.Enabled = false; w.Dispose(); }
            _watchers.Clear();
        }
    }
}
