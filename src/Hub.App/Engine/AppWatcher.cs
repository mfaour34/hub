using System.Diagnostics;
using Hub.App.Native;
using Hub.Core.Audio;

namespace Hub.App.Engine;

/// <summary>
/// Keeps the engine's captures in step with the apps playing to the sink device: new apps are captured on their
/// channel, apps that quit or move to another device are dropped. A child process whose parent is captured is
/// skipped, since process loopback already includes the parent's whole tree.
/// </summary>
public sealed class AppWatcher(AudioEngine engine, string sinkGuid, Func<string, AudioChannel> channelFor, Func<string, bool>? include = null)
{
    readonly Dictionary<int, AudioChannel> _captured = [];
    readonly HashSet<int> _failed = [];
    readonly Dictionary<int, string> _names = [];
    readonly int _self = Environment.ProcessId;
    HashSet<int> _lastSessions = [];
    HashSet<int> _lastWanted = [];

    public string? LastError { get; private set; }

    public void Poll()
    {
        var sessions = AudioSessions.List(sinkGuid, _names)
            .Where(s => s.ProcessId != _self && (include?.Invoke(s.ProcessName) ?? true)).ToList();
        var pids = sessions.Select(s => s.ProcessId).ToHashSet();
        HashSet<int> wanted;
        if (pids.SetEquals(_lastSessions)) wanted = _lastWanted;
        else
        {
            // the process tree only matters when the set of apps changed
            wanted = [.. pids];
            var parents = ProcessTree.Parents();
            wanted.RemoveWhere(pid => ProcessTree.HasAncestorIn(pid, parents, pids));
            foreach (var gone in _names.Keys.Where(p => !pids.Contains(p)).ToList()) _names.Remove(gone); // pids get reused
            _lastSessions = pids;
            _lastWanted = wanted;
        }
        var names = sessions.GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.First().ProcessName);

        foreach (var pid in _captured.Keys.Where(p => !wanted.Contains(p)).ToList())
        {
            engine.RemoveApp(pid);
            _captured.Remove(pid);
        }
        _failed.IntersectWith(wanted);
        foreach (var pid in wanted)
        {
            var channel = channelFor(names[pid]);
            if (_captured.TryGetValue(pid, out var current))
            {
                if (current != channel) { engine.SetAppChannel(pid, channel); _captured[pid] = channel; }
                continue;
            }
            if (_failed.Contains(pid)) continue;
            try
            {
                engine.AddApp(pid, channel).GetAwaiter().GetResult();
                _captured[pid] = channel;
            }
            catch (Exception ex)
            {
                _failed.Add(pid); // e.g. a protected process; don't retry every poll
                LastError = $"{names[pid]}: {ex.Message}";
            }
        }
        engine.CollectGarbage();
    }

    public IReadOnlyDictionary<int, AudioChannel> Captured => _captured;
}
