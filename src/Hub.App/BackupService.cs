using Hub.App.Native;
using Hub.Core.Backup;
using Hub.Core.Razer;

namespace Hub.App;

/// <summary>
/// Backs audio and mouse settings up to the user's private GitHub repo, restores them, and exports them to a folder.
/// GitHub is only contacted when the user presses a button. The token lives in Windows Credential Manager and never
/// leaves this class except in the Authorization header to api.github.com.
/// </summary>
public sealed class BackupService(AppSettings settings, AudioService audio, DeviceManager devices)
{
    /// <summary>Restores read a backup by this id to undo the last restore from the copy taken just before it.</summary>
    public const string BeforeRestoreId = "before-restore";

    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static string BeforeRestoreDir => Path.Combine(AppSettings.DataDir, "before-restore");

    readonly SemaphoreSlim _busy = new(1, 1);
    BackupSettings State => settings.Backup;

    // ---------------- status and linking ----------------

    public object Status() => new
    {
        linked = State.Repo is not null,
        repo = State.Repo,
        lastBackup = State.LastBackup,
        changedSince = State.ChangedSince,
        remind = State.Remind,
        tokenExpires = State.TokenExpires,
        canUndo = File.Exists(Path.Combine(BeforeRestoreDir, BackupFiles.AudioFile)),
    };

    /// <summary>Checks the repo and token with GitHub, then stores the token. Nothing is stored if a check fails.</summary>
    public async Task Link(string repo, string token)
    {
        repo = GitHubBackup.CheckRepo(repo);
        var check = await new GitHubBackup(Http, repo, token).CheckAsync();
        CredentialStore.Save(check.Repo, GitHubBackup.CheckToken(token));
        State.Repo = check.Repo;
        State.Branch = check.Branch;
        State.TokenExpires = check.TokenExpires;
        State.ChangedSince ??= DateTimeOffset.Now; // nothing of this PC is in the repo yet
        settings.Save();
    }

    public void Unlink()
    {
        CredentialStore.Delete();
        State.Repo = null;
        State.TokenExpires = null;
        settings.Save();
    }

    GitHubBackup Client()
    {
        if (State.Repo is not { } repo) throw new InvalidOperationException("Link a GitHub repo first");
        var token = CredentialStore.Read() ?? throw new InvalidOperationException("The token is gone from Windows Credential Manager. Unlink and link the repo again.");
        return new GitHubBackup(Http, repo, token);
    }

    // ---------------- change tracking and reminders ----------------

    public void MarkChanged()
    {
        if (State.ChangedSince is not null) return;
        State.ChangedSince = DateTimeOffset.Now;
        settings.Save();
    }

    public void SetRemind(bool on)
    {
        State.Remind = on;
        settings.Save();
    }

    /// <summary>The tray reminder to show now, if any: at most once a day, a day after an unsaved change, or a week before the token expires.</summary>
    public string? DueReminder()
    {
        var now = DateTimeOffset.Now;
        if (State.Repo is null || !State.Remind || now - State.LastReminder < TimeSpan.FromDays(1)) return null;
        string? text =
            State.TokenExpires is { } exp && exp - now < TimeSpan.FromDays(7)
                ? exp < now ? "Your Hub backup token has expired. Make a new one and link the repo again in Settings → Backup."
                    : $"Your Hub backup token expires {exp.LocalDateTime:d MMM}. Make a new one and link the repo again in Settings → Backup."
            : State.ChangedSince is { } since && now - since >= TimeSpan.FromDays(1)
                ? "Your audio or mouse settings changed and aren't backed up. Open Hub → Settings → Backup."
            : null;
        if (text is null) return null;
        State.LastReminder = now;
        settings.Save();
        return text;
    }

    // ---------------- backing up ----------------

    /// <summary>The files of a backup or an export: audio.json, mouse.json and the Equalizer APO presets.</summary>
    /// <remarks>MouseProblem says why mouse.json is missing or incomplete.</remarks>
    public async Task<(SortedDictionary<string, string> Files, string? MouseProblem)> CollectFiles()
    {
        var audioBackup = await Sta.Run(audio.ToBackup);
        var inUse = await Sta.Run(audio.InUseProfiles);
        var (mouse, problem) = await ReadMouse();
        var files = BackupFiles.Build(audioBackup, mouse, inUse, AudioService.ApoText);
        BackupFiles.Parse(files[BackupFiles.AudioFile], files.GetValueOrDefault(BackupFiles.MouseFile)); // never write a backup restore would refuse
        return (files, problem);
    }

    public sealed record BackupResult(bool Committed, bool MouseIncluded, string? Note);

    public async Task<BackupResult> BackupNow()
    {
        await _busy.WaitAsync();
        try
        {
            var gh = Client();
            State.TokenExpires = await gh.EnsurePrivateAsync();
            var started = DateTimeOffset.Now;
            var (files, mouseProblem) = await CollectFiles();
            string? note = mouseProblem;
            if (!files.ContainsKey(BackupFiles.MouseFile))
            {
                // the mouse is off or asleep: keep the mouse settings of the last backup instead of dropping them
                var last = (await gh.ListAsync(State.Branch)).FirstOrDefault();
                if (last is not null && await gh.ReadAsync(last.Sha, BackupFiles.MouseFile) is { } previous)
                {
                    BackupFiles.Parse(files[BackupFiles.AudioFile], previous);
                    files[BackupFiles.MouseFile] = previous;
                    note = $"{mouseProblem} The mouse settings from the last backup were kept.";
                }
                else note = $"{mouseProblem} This backup has no mouse settings.";
            }
            var sha = await gh.PushAsync(State.Branch, files, $"{GitHubBackup.CommitPrefix} from {Environment.MachineName}");
            State.LastBackup = DateTimeOffset.Now;
            if (State.ChangedSince <= started) State.ChangedSince = null; // a change made while backing up stays unsaved
            settings.Save();
            return new BackupResult(sha is not null, files.ContainsKey(BackupFiles.MouseFile), note);
        }
        finally { _busy.Release(); }
    }

    // ---------------- restoring ----------------

    public sealed record BackupEntry(string Id, DateTimeOffset Date, string Label);

    /// <summary>The repo's backups, newest first, after the copy taken before the last restore (when there is one).</summary>
    public async Task<List<BackupEntry>> List()
    {
        var list = new List<BackupEntry>();
        var undo = Path.Combine(BeforeRestoreDir, BackupFiles.AudioFile);
        if (File.Exists(undo)) list.Add(new(BeforeRestoreId, File.GetLastWriteTime(undo), "Your settings just before the last restore (this PC)"));
        if (State.Repo is not null)
            list.AddRange((await Client().ListAsync(State.Branch)).Select(c =>
                new BackupEntry(c.Sha, c.Date, c.Message.Length > GitHubBackup.CommitPrefix.Length ? c.Message[GitHubBackup.CommitPrefix.Length..].Trim() : "")));
        return list;
    }

    async Task<(AudioBackup Audio, MouseBackup? Mouse)> Load(string id)
    {
        if (id == BeforeRestoreId)
        {
            var dir = BeforeRestoreDir;
            var mouseFile = Path.Combine(dir, BackupFiles.MouseFile);
            return BackupFiles.Parse(await File.ReadAllTextAsync(Path.Combine(dir, BackupFiles.AudioFile)),
                File.Exists(mouseFile) ? await File.ReadAllTextAsync(mouseFile) : null);
        }
        var gh = Client();
        var audioJson = await gh.ReadAsync(id, BackupFiles.AudioFile) ?? throw new InvalidDataException("That commit isn't a Hub backup (no audio.json)");
        return BackupFiles.Parse(audioJson, await gh.ReadAsync(id, BackupFiles.MouseFile));
    }

    /// <summary>What restoring the backup would change, in plain lines.</summary>
    public async Task<List<string>> Preview(string id)
    {
        var (a, m) = await Load(id);
        var (mouse, problem) = await ReadMouse();
        var lines = BackupFiles.Diff(await Sta.Run(audio.ToBackup), mouse, a, m);
        if (m is not null && mouse is null && devices.Mouse is not null)
        {
            // connected but unreadable: say why rather than "not connected"
            lines.RemoveAll(l => l.StartsWith("Mouse settings: skipped", StringComparison.Ordinal));
            lines.Add($"Mouse settings: skipped. {problem} Hub only writes the mouse when it can keep a copy to undo.");
        }
        return lines;
    }

    /// <summary>Saves the current settings on this PC, then puts the backup's in place. Returns what couldn't be restored.</summary>
    public async Task<List<string>> Restore(string id)
    {
        await _busy.WaitAsync();
        try
        {
            var (a, m) = await Load(id);
            var currentAudio = await Sta.Run(audio.ToBackup);
            var (currentMouse, mouseProblem) = await ReadMouse();
            // the undo copy must itself be restorable, or the restore can't be undone
            try { BackupFiles.Parse(BackupJson.Write(currentAudio), currentMouse is null ? null : BackupJson.Write(currentMouse)); }
            catch (InvalidDataException e) { throw new InvalidOperationException($"Your current settings can't be saved for undo ({e.Message}), so nothing was restored."); }

            // the undo copy (not when undoing, so the undo copy stays the state before the restore)
            if (id != BeforeRestoreId)
            {
                Directory.CreateDirectory(BeforeRestoreDir);
                await File.WriteAllTextAsync(Path.Combine(BeforeRestoreDir, BackupFiles.AudioFile), BackupJson.Write(currentAudio));
                var mouseFile = Path.Combine(BeforeRestoreDir, BackupFiles.MouseFile);
                if (currentMouse is not null) await File.WriteAllTextAsync(mouseFile, BackupJson.Write(currentMouse));
                else File.Delete(mouseFile);
            }

            await Sta.Run(() => audio.RestoreBackup(a));
            var notes = new List<string>();
            if (m is not null && currentMouse is null && devices.Mouse is not null)
                notes.Add($"Mouse settings weren't restored: {mouseProblem} Hub only writes the mouse when it can keep a copy to undo.");
            else if (m is not null) notes.AddRange(await WriteMouse(m));
            MarkChanged();
            return notes;
        }
        finally { _busy.Release(); }
    }

    // ---------------- the mouse ----------------

    /// <summary>
    /// The mouse's onboard settings, or null with the reason when it isn't connected or doesn't answer. Parts Hub
    /// couldn't restore (a button the vendor app set to something Hub doesn't know, an odd curve) are left out, with a note.
    /// </summary>
    async Task<(MouseBackup? Mouse, string? Problem)> ReadMouse()
    {
        if (devices.Mouse is not { } m || devices.MouseCaps is not { } c || devices.MouseModel is not { } model)
            return (null, "The mouse isn't connected.");
        try
        {
            return await Task.Run<(MouseBackup?, string?)>(() =>
            {
                var left = new List<string>();
                var b = new MouseBackup { Model = model.FullName };
                if (c.DpiStages)
                {
                    var s = m.GetDpiStages();
                    b.DpiStages = s.Stages.Select(x => x.X).ToList();
                    b.ActiveStage = s.ActiveStage;
                }
                if (c.Polling) b.PollingHz = m.GetPollingRate();
                if (c.PowerSaving)
                {
                    b.IdleMinutes = Math.Clamp((int)m.GetIdleTimeout().TotalMinutes, 1, 15);
                    b.LowBatteryPercent = Math.Clamp((int)Math.Round(m.GetLowBatteryThresholdPercent() / 5.0) * 5, 5, 100);
                }
                if (c.LiftOff)
                {
                    var (asym, tracking) = m.GetLiftMode();
                    var d = asym ? m.GetLiftOff() : new LiftOff(2, 1);
                    b.Lift = new LiftBackup(asym, Math.Clamp(tracking, 1, 3), d.LiftOffDistance, d.LandingDistance);
                }
                if (c.DynamicSensitivity)
                {
                    var points = settings.CustomCurve.Select(p => new[] { p.X, p.Y }).ToList();
                    if (!BackupFiles.IsCurve(points)) { points = []; left.Add("the custom curve"); }
                    b.DynamicSensitivity = new DynamicSensitivityBackup(m.GetDynamicSensitivityEnabled(), (int)m.GetDynamicSensitivityMode(), points);
                }
                if (c.Rotation)
                {
                    var (enabled, angle) = m.GetRotation();
                    b.Rotation = new RotationBackup(enabled, angle);
                }
                foreach (var id in c.Buttons.Where(id => id != ViperButtons.Left))
                {
                    var a = m.GetButton(id);
                    var button = new ButtonBackup((int)a.Function, a.Data.Select(x => (int)x).ToArray());
                    if (BackupFiles.IsButton(button)) b.Buttons[id.ToString()] = button;
                    else left.Add(ViperButtons.Names.GetValueOrDefault(id, $"button {id}"));
                }
                try { BackupFiles.Validate(b); }
                catch (InvalidDataException e) { return (null, $"The mouse's settings couldn't be read ({e.Message})."); }
                return (b, left.Count == 0 ? null : $"Left out of the mouse settings: {string.Join(", ", left)} (set to something Hub can't restore).");
            });
        }
        catch (Exception e) { return (null, $"The mouse didn't answer ({e.Message}). Wake it and try again."); }
    }

    /// <summary>Writes a backup's mouse settings, part by part, to the same mouse model. Returns what was skipped.</summary>
    async Task<List<string>> WriteMouse(MouseBackup b)
    {
        if (devices.Mouse is not { } m || devices.MouseCaps is not { } c || devices.MouseModel is not { } model)
            return ["Mouse settings weren't restored: the mouse isn't connected. Restore again once it is."];
        if (model.FullName != b.Model) return [$"Mouse settings weren't restored: the backup is from a {b.Model}."];
        var notes = new List<string>();
        void Part(string what, bool supported, Action write)
        {
            if (!supported) return;
            try { write(); }
            catch (Exception e) { notes.Add($"Mouse {what} wasn't restored: {e.Message}"); }
        }
        await Task.Run(() =>
        {
            Part("DPI", c.DpiStages && b.DpiStages.Count > 0, () =>
            {
                var stages = b.DpiStages.Select(d => new DpiStage(d, d)).ToList();
                m.SetDpiStages(new DpiStages(b.ActiveStage, stages));
                m.SetDpi(stages[b.ActiveStage - 1]);
            });
            Part("polling rate", c.Polling && b.PollingHz is not null, () =>
            {
                if (!m.SupportedPollingRates.Contains(b.PollingHz!.Value)) throw new InvalidOperationException($"{b.PollingHz} Hz isn't available on this connection");
                m.SetPollingRate(b.PollingHz.Value);
            });
            Part("power settings", c.PowerSaving && b.IdleMinutes is not null && b.LowBatteryPercent is not null, () =>
            {
                m.SetIdleTimeout(TimeSpan.FromMinutes(b.IdleMinutes!.Value));
                m.SetLowBatteryThresholdPercent(b.LowBatteryPercent!.Value);
            });
            Part("lift-off", c.LiftOff && b.Lift is not null, () =>
            {
                if (b.Lift!.Asymmetric) m.SetAsymmetricLiftOff(new LiftOff(b.Lift.LiftOff, b.Lift.Landing));
                else m.SetSymmetricLiftOff(b.Lift.Tracking);
            });
            Part("Dynamic Sensitivity", c.DynamicSensitivity && b.DynamicSensitivity is not null, () =>
            {
                var d = b.DynamicSensitivity!;
                var points = d.Points.Select(p => new CurvePoint(p[0], p[1])).ToList();
                // the curve or mode first, so a failure there doesn't leave Dynamic Sensitivity switched on in the old mode
                if (d.Enabled)
                {
                    if ((DynamicSensitivityMode)d.Mode == DynamicSensitivityMode.Custom && points.Count > 0) m.SetCustomCurve(points);
                    else m.SetDynamicSensitivityMode((DynamicSensitivityMode)d.Mode);
                }
                m.SetDynamicSensitivityEnabled(d.Enabled);
                if (points.Count > 0)
                {
                    settings.CustomCurve = points.Select(p => new CurvePointDto(p.X, p.Y)).ToList();
                    try { settings.Save(); } catch (IOException) { } // the mouse has it; settings.json catches up on the next save
                }
            });
            Part("rotation", c.Rotation && b.Rotation is not null, () => m.SetRotation(b.Rotation!.Enabled, b.Rotation.Angle));
            foreach (var (key, button) in b.Buttons)
            {
                var id = byte.Parse(key);
                Part($"button {key}", c.Buttons.Contains(id) && id != ViperButtons.Left, () =>
                    m.SetButton(new ButtonAssignment(id, (ButtonFunction)button.Function, button.Data.Select(x => (byte)x).ToArray())));
            }
        });
        devices.RefreshSummaries();
        return notes;
    }
}
