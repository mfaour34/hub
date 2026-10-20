namespace Hub.App.Native;

/// <summary>
/// Runs work on a fresh single-threaded-apartment thread. Windows' audio endpoint COM objects (IPropertyStore writes,
/// IPolicyConfig visibility, AudioPolicyConfig) fail from thread-pool (MTA) threads, so all audio device work goes through here.
/// </summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Hub audio (STA)" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static Task Run(Action work) => Run(() => { work(); return 0; });
}
