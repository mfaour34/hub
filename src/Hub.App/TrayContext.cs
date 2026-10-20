using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Hub.App;

public sealed class TrayContext : ApplicationContext
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly DeviceManager _devices;
    readonly AudioService _audio;
    readonly Bridge _bridge;
    readonly NotifyIcon _tray;
    readonly Icon _appIcon;
    readonly Bitmap _logo16, _logo32;
    readonly ToolStripMenuItem _mouseItem = new("Mouse: not connected") { Enabled = false };
    readonly ToolStripMenuItem _headsetItem = new("Headset: not connected") { Enabled = false };
    readonly ToolStripMenuItem _eqMenu = new("Headset EQ");
    MainWindow? _window;
    Icon? _trayIcon;

    public TrayContext()
    {
        using (var s = typeof(TrayContext).Assembly.GetManifestResourceStream("Hub.App.Hub.ico")!)
            _appIcon = new Icon(s);
        _logo16 = new Icon(_appIcon, 16, 16).ToBitmap();
        _logo32 = new Icon(_appIcon, 32, 32).ToBitmap();

        _devices = new DeviceManager(_settings);
        _audio = new AudioService(_settings);
        _bridge = new Bridge(_devices, _audio, _settings);
        _devices.Changed += Refresh;
        _devices.Notice += msg => _tray!.ShowBalloonTip(5000, "Hub", msg, ToolTipIcon.Warning);

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.AddRange([
            new ToolStripMenuItem("Open Hub", null, (_, _) => ShowWindow()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) },
            new ToolStripSeparator(),
            _mouseItem, _headsetItem, new ToolStripSeparator(),
            _eqMenu,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) => BuildEqMenu();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Hub", Icon = _appIcon };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        Refresh();
        _devices.Start();

        // Apply the saved audio mix once VoiceMeeter is up (it is started by Windows at logon).
        // and refresh the Equalizer APO files, so EQ/limiter updates from a new Hub version reach the audio engine.
        _ = Native.Sta.Run(() =>
        {
            if (!_audio.GetStatus().Routed) return;
            try { _audio.ApplySampleRates(); } catch (Exception) { }
            try { _audio.WriteEqualizerApo(); } catch (Exception) { }
            try { _audio.ApplyFaders(); } catch (Exception) { }
        });

        var vendors = DeviceManager.RunningVendorApps();
        if (vendors.Count > 0)
            _tray.ShowBalloonTip(6000, "Hub",
                $"{string.Join(", ", vendors)} still running and may override your settings. Quit them and disable their autostart.",
                ToolTipIcon.Info);
    }

    void BuildEqMenu()
    {
        _eqMenu.DropDownItems.Clear();
        _eqMenu.Enabled = _devices.Headset is { HeadsetConnected: true };
        foreach (var preset in _settings.HeadsetEqPresets)
            _eqMenu.DropDownItems.Add(preset.Name, null, async (_, _) =>
            {
                try
                {
                    await _devices.Headset!.SetEqAsync(preset.Gains, saveToOnboard: true);
                    _devices.RefreshSummaries();
                }
                catch (Exception ex) { _tray.ShowBalloonTip(3000, "Headset EQ", ex.Message, ToolTipIcon.Error); }
            });
    }

    void Refresh()
    {
        var m = _devices.MouseBattery;
        var h = _devices.HeadsetBattery;
        _mouseItem.Text = _devices.Mouse is null ? "Mouse: not connected"
            : m is null ? "Mouse: asleep" : $"Mouse: {m.Percent}%{(m.Charging ? " (charging)" : "")}";
        _headsetItem.Text = _devices.Headset is null ? "Headset: dongle not found"
            : h is null ? "Headset: off" : $"Headset: {h.Percent}%{(h.Charging ? " (charging)" : "")}";
        var tip = $"Hub\n{_mouseItem.Text}\n{_headsetItem.Text}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        bool low = (m is { Charging: false } && m.Percent <= _settings.LowBatteryNotifyPercent)
                || (h is { Charging: false } && h.Percent <= _settings.LowBatteryNotifyPercent);
        var old = _trayIcon;
        _trayIcon = TrayIcon(low);
        _tray.Icon = _trayIcon;
        old?.Dispose();
        _window?.PushState();
    }

    /// <summary>The Hub logo, with a red badge while a device is at or below the alert level.</summary>
    Icon TrayIcon(bool lowBattery)
    {
        int size = SystemInformation.SmallIconSize.Width > 16 ? 32 : 16;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(size == 32 ? _logo32 : _logo16, 0, 0, size, size);
            if (lowBattery)
            {
                float d = size * 0.5f;
                using var ring = new SolidBrush(Color.FromArgb(0x0d, 0x0f, 0x14));
                g.FillEllipse(ring, size - d - 1, size - d - 1, d + 1, d + 1);
                using var badge = new SolidBrush(Color.FromArgb(0xff, 0x5d, 0x6c));
                g.FillEllipse(badge, size - d, size - d, d - 1, d - 1);
            }
        }
        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public async Task CaptureAndExitAsync(string dir, string[] pages)
    {
        ShowWindow();
        try
        {
            await _window!.CapturePagesAsync(dir, pages.Length > 0 ? pages : ["overview", "mouse:customize", "mouse:performance",
                "mouse:power", "mouse:calibration", "mouse:advanced", "headset", "audio", "settings"]);
        }
        finally { ExitThread(); }
    }

    void ShowWindow()
    {
        if (_window is { IsDisposed: false })
        {
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Activate();
            return;
        }
        _window = new MainWindow(_bridge, _appIcon);
        _window.FormClosed += (_, _) => _window = null; // free WebView2 memory while Hub sits in the tray
        _window.Show();
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _window?.Close();
        _devices.Dispose();
        _audio.Dispose();
        base.ExitThreadCore();
    }
}
