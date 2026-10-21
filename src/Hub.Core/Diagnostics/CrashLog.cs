using System.Text.Json;

namespace Hub.Core.Diagnostics;

/// <summary>
/// The last 7 days of <see cref="StabilityEvent"/>s as JSON lines. Small by nature (a few hundred bytes per event,
/// at most <see cref="MaxEntries"/>), rewritten whole on every change through a flushed temp file.
/// </summary>
public sealed class CrashLog(string path)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>One incident often reaches two logs (WHEA-Logger and a WHEA live dump, Display 4101 and a WATCHDOG dump).</summary>
    public static readonly TimeSpan SameIncident = TimeSpan.FromMinutes(2);

    /// <summary>Upper bound on stored entries, whatever floods the logs.</summary>
    public const int MaxEntries = 500;

    readonly object _gate = new();
    List<StabilityEvent>? _events;

    /// <summary>Newest event time stored, the point the start-up catch-up reads the Windows logs from.</summary>
    public DateTimeOffset? Newest { get { lock (_gate) return Events().Count == 0 ? null : Events().Max(e => e.Time); } }

    /// <summary>Stores events not seen before (by key) and within the window; returns how many were added.</summary>
    public int Add(IEnumerable<StabilityEvent> events, DateTimeOffset now)
    {
        lock (_gate)
        {
            var list = Events();
            var keys = list.Select(e => e.Key).ToHashSet();
            int added = 0;
            foreach (var e in events)
                if (e.Time >= now - Window && keys.Add(e.Key) && !list.Any(x => SameIncidentAs(x, e))) { list.Add(e); added++; }
            int before = list.Count;
            list.RemoveAll(e => e.Time < now - Window);
            if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries); // oldest first (list is in time order)
            if (added > 0 || list.Count != before) Write(list);
            return added;
        }
    }

    /// <summary>The same kind within a couple of minutes, reported by the other log (System vs. Windows Error Reporting).</summary>
    static bool SameIncidentAs(StabilityEvent a, StabilityEvent b) =>
        a.Kind == b.Kind && a.Log != b.Log && (a.Time - b.Time).Duration() <= SameIncident;

    /// <summary>Events of the last 7 days, newest first.</summary>
    public IReadOnlyList<StabilityEvent> Recent(DateTimeOffset now)
    {
        lock (_gate) return Events().Where(e => e.Time >= now - Window).OrderByDescending(e => e.Time).ToList();
    }

    List<StabilityEvent> Events()
    {
        if (_events is not null) return _events;
        var loaded = new List<StabilityEvent>();
        try
        {
            if (File.Exists(path))
                foreach (var line in File.ReadLines(path))
                {
                    try { if (JsonSerializer.Deserialize<StabilityEvent>(line) is { Key: not null } e) loaded.Add(e); }
                    catch (JsonException) { /* a line cut short by a crash; the rest of the file is still good */ }
                }
        }
        catch (IOException) { return loaded; } // unreadable right now: don't cache, so a later call reads the whole file
        loaded.Sort((a, b) => a.Time.CompareTo(b.Time));
        return _events = loaded;
    }

    /// <summary>Best effort: a scanner or backup tool holding the file must not lose the entry, which stays in memory.</summary>
    void Write(List<StabilityEvent> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new StreamWriter(f))
            {
                foreach (var e in list.OrderBy(e => e.Time)) w.WriteLine(JsonSerializer.Serialize(e));
                w.Flush();
                f.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* written again with the next change */ }
    }
}
