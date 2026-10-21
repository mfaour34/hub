using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Hub.App;

/// <summary>The Hub window: a WebView2 page (wwwroot) served from the install folder, talking to <see cref="Bridge"/>.</summary>
public sealed class MainWindow : Form
{
    readonly Bridge _bridge;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public MainWindow(Bridge bridge, Icon icon)
    {
        _bridge = bridge;
        Text = "Hub";
        Icon = icon;
        BackColor = bridge.WindowBackground;
        _web.DefaultBackgroundColor = bridge.WindowBackground;
        Size = new Size(1280, 820);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        HandleCreated += (_, _) => UseDarkTitleBar();
        bridge.AppearanceChanged += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(ApplyWindowColours); };
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hub", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        // the page starts in its theme instead of flashing the stylesheet's default before settings arrive
        await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__hubAppearance = {_bridge.AppearanceJson()};");
        core.SetVirtualHostNameToFolderMapping("hub.app", Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += async (_, e) =>
        {
            var reply = await _bridge.HandleAsync(e.WebMessageAsJson);
            if (!IsDisposed) core.PostWebMessageAsJson(reply);
        };
        core.Navigate("https://hub.app/index.html");
    }

    /// <summary>Dev aid (`Hub.exe --shots dir`): renders each page to PNG through WebView2's own capture.</summary>
    public async Task CapturePagesAsync(string dir, params string[] pages)
    {
        while (_web.CoreWebView2 is null) await Task.Delay(100);
        var done = new TaskCompletionSource();
        _web.CoreWebView2.NavigationCompleted += (_, _) => done.TrySetResult();
        await done.Task;
        await Task.Delay(1500);
        Directory.CreateDirectory(dir);
        foreach (var page in pages)
        {
            // "name=script" runs arbitrary UI script (e.g. opening a dialog); "page:tab" picks a mouse tab.
            var (name, script) = page.Split('=', 2) is [var n, var js] ? (n, js)
                : page.Split(':') is [var p, var tab]
                    ? ($"{p}-{tab}", $"showPage('{p}'); document.querySelector('#mouseTabs [data-tab={tab}]')?.click();")
                    : (page, $"showPage('{page}');");
            await _web.CoreWebView2.ExecuteScriptAsync(script);
            await Task.Delay(4000);
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }
    }

    public void PushState()
    {
        if (_web.CoreWebView2 is { } core) core.PostWebMessageAsJson(_bridge.StateJson());
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Window, WebView and title bar in the theme's background (after an appearance change too).</summary>
    void ApplyWindowColours()
    {
        BackColor = _bridge.WindowBackground;
        _web.DefaultBackgroundColor = BackColor;
        UseDarkTitleBar();
    }

    /// <summary>Title bar in the theme's background with readable text (caption colours are Windows 11 only).</summary>
    void UseDarkTitleBar()
    {
        var bg = _bridge.WindowBackground;
        bool dark = bg.GetBrightness() < 0.5f;
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = bg.R | bg.G << 8 | bg.B << 16; // COLORREF is BGR
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        int text = dark ? 0x00E2E9EC : 0x001B1B1B;
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
    }
}
