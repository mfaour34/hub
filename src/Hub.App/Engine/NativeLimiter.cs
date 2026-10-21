using System.Runtime.InteropServices;
using Hub.Core.Audio.Engine;

namespace Hub.App.Engine;

/// <summary>The look-ahead limiter (native/hub-limiter/limiter.c, built as hub-dsp.dll).</summary>
public sealed unsafe class NativeLimiter : IBlockLimiter
{
    IntPtr _handle;

    /// <param name="threshold">
    /// Linear threshold; 1.0 = full scale. Below 1.0 the limiter holds loud peaks (gunshots, explosions) that much
    /// lower while everything quieter passes unchanged ("tame loud sounds").
    /// </param>
    public NativeLimiter(int sampleRate, double threshold = 1.0)
    {
        _handle = hub_limiter_create(sampleRate, threshold);
        if (_handle == IntPtr.Zero) throw new OutOfMemoryException("Limiter allocation failed");
    }

    public void Process(Span<float> left, Span<float> right)
    {
        fixed (float* l = left, r = right) hub_limiter_process(_handle, l, r, l, r, left.Length);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        hub_limiter_destroy(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport("hub-dsp.dll")] static extern IntPtr hub_limiter_create(float sampleRate, double threshold);
    [DllImport("hub-dsp.dll")] static extern void hub_limiter_destroy(IntPtr l);
    [DllImport("hub-dsp.dll")] static extern void hub_limiter_process(IntPtr l, float* inL, float* inR, float* outL, float* outR, int frames);
}
