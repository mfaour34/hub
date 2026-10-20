namespace Hub.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--tidy", ..])
            return SelfTest.RunTidy(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "hub-tidy.log"));
        if (args is ["--audiotest", ..])
            return SelfTest.RunAudio(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "hub-audiotest.log"));
        if (args is ["--cleanup", ..]) // uninstaller: undo Hub's audio changes and autostart
        {
            try
            {
                Bridge.Autostart = false;
                var audio = new AudioService(AppSettings.Load());
                Native.Sta.Run(audio.Cleanup).GetAwaiter().GetResult();
            }
            catch (Exception ex) { LogCrash(ex); return 1; }
            return 0;
        }
        if (args is ["--selftest", ..])
            return SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "hub-selftest.log"));

        using var single = new Mutex(true, @"Local\Hub", out bool first);
        if (!first) return 0;

        // A tray app has nowhere to show a crash: write it to %APPDATA%\Hub\logs so it can be reported.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        // Device events are posted to the captured context; make sure it is the UI thread before anything starts.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var tray = new TrayContext();
        if (args is ["--shots", var dir, ..])
            Application.Idle += StartCapture;
        Application.Run(tray);

        void StartCapture(object? s, EventArgs e)
        {
            Application.Idle -= StartCapture;
            _ = tray.CaptureAndExitAsync(args[1], args[2..]);
        }
        return 0;
    }

    public static string Version =>
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    static void LogCrash(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(AppSettings.DataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:s}] Hub {Version}\n{ex}\n\n");
        }
        catch (Exception) { }
    }
}
