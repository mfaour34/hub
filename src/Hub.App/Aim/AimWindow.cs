using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Hub.App.Aim;

/// <summary>
/// The aim lab: its own process (`Hub.exe --aim`) with a borderless full-screen window, so closing it gives back all
/// of its memory and nothing of it runs inside Hub. One at a time: a second launch brings the first one forward.
/// </summary>
public sealed class AimWindow : Form
{
    const string MutexName = @"Local\HubAim", ShowEventName = @"Local\HubAimShow";

    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly AimBridge _bridge;
    readonly string _appearance;

    AimWindow(int? dpi, bool accel, Screen screen, string appearance)
    {
        _bridge = new AimBridge(dpi, accel, () => BeginInvoke(Close));
        _appearance = appearance;
        Text = "Hub aim lab";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.Black;
        _web.DefaultBackgroundColor = Color.Black;
        Controls.Add(_web);
        using (var s = typeof(AimWindow).Assembly.GetManifestResourceStream("Hub.App.Hub.ico")) if (s is not null) Icon = new Icon(s);
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hub", "WebView2-aim");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__hubAppearance = {_appearance};");
        core.SetVirtualHostNameToFolderMapping("hub.app", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += async (_, e) =>
        {
            var reply = await _bridge.HandleAsync(e.WebMessageAsJson);
            if (!IsDisposed) core.PostWebMessageAsJson(reply);
        };
        core.Navigate("https://hub.app/aim.html");
        if (_shots is not null) _ = CaptureAsync(_shots);
    }

    string? _shots;

    /// <summary>Dev aid (`--aim --shots dir`): the menu and a flick round in progress, as PNGs; then exits.</summary>
    async Task CaptureAsync(string dir)
    {
        var core = _web.CoreWebView2;
        var loaded = new TaskCompletionSource();
        core.NavigationCompleted += (_, _) => loaded.TrySetResult();
        await loaded.Task;
        await Task.Delay(2000);
        Directory.CreateDirectory(dir);
        async Task Shot(string name)
        {
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
        await Shot("aim-menu");
        await core.ExecuteScriptAsync("start(flickRound(30, 'flick', () => {})); yaw = 4; pitch = 2;");
        await Task.Delay(1500);
        await Shot("aim-flick");
        File.WriteAllText(Path.Combine(dir, "aim-errors.json"), await core.ExecuteScriptAsync("JSON.stringify(window.__aimErrors)"));
        Close();
    }

    /// <summary>`Hub.exe --aim [--dpi n] [--accel 0|1] [--monitor \\.\DISPLAYn]`.</summary>
    public static int Run(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        using var single = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // this process was started by Hub, which was in front, so it may hand the foreground to the open aim lab
            AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) using (show) show.Set();
            return 0;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        ApplicationConfiguration.Initialize();
        int? dpi = int.TryParse(Arg("--dpi"), out var d) && d > 0 ? d : null;
        bool accel = Arg("--accel") == "1";
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Arg("--monitor")) ?? Screen.PrimaryScreen!;
        var window = new AimWindow(dpi, accel, screen, Bridge.AppearanceJson(AppSettings.Load())) { _shots = Arg("--shots") };

        // a second launch signals this event: bring the window forward
        var waiter = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            try { if (window.IsHandleCreated) window.BeginInvoke(() => { window.WindowState = FormWindowState.Normal; window.Activate(); SetForegroundWindow(window.Handle); }); }
            catch (InvalidOperationException) { /* closing */ }
        }, null, -1, false);
        Application.Run(window);
        waiter.Unregister(null);
        return 0;
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
