/*
 * Tests for the Hub limiter (limiter.c). Build and run:
 *   zig cc -O2 test_limiter.c limiter.c -lm -o test_limiter && ./test_limiter
 *
 * Original work, Copyright (c) 2026 the Hub authors. Released under the MIT License.
 */
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "limiter.h"

#define CEILING 0.99997
static const double PI = 3.14159265358979323846;
static int failures = 0;

#define CHECK(cond, ...) do { \
    int ok_ = (cond); \
    printf(ok_ ? "ok    " : "FAIL  "); printf(__VA_ARGS__); printf("\n"); \
    if (!ok_) failures++; \
} while (0)

/* deterministic white noise in [-1, 1] */
static uint32_t rng = 1;
static double noise(void) { rng = rng * 1664525u + 1013904223u; return (rng >> 8) / 8388607.5 - 1.0; }

typedef struct { float *l, *r; int n; } Stereo;
static Stereo alloc_stereo(int n) { Stereo s = { calloc(n, sizeof(float)), calloc(n, sizeof(float)), n }; return s; }
static void free_stereo(Stereo s) { free(s.l); free(s.r); }

/* ---- signal generators (fs-aware) ---- */
static void gen_sine(Stereo s, double fs, double freq, double amp) {
    for (int i = 0; i < s.n; i++) s.l[i] = s.r[i] = (float)(amp * sin(2 * PI * freq * i / fs));
}
static void gen_bursts(Stereo s, double fs, double loud) { /* 300 ms of loud stereo noise, 700 ms quiet */
    rng = 12345;
    int period = (int)(1.0 * fs), on = (int)(0.3 * fs);
    for (int i = 0; i < s.n; i++) {
        double a = (i % period) < on ? loud : 0.03;
        s.l[i] = (float)(a * noise());
        s.r[i] = (float)(a * noise());
    }
}
static void gen_hf_noise(Stereo s, double amp) { /* alternating-sign noise: energy near Nyquist, big inter-sample peaks */
    rng = 777;
    for (int i = 0; i < s.n; i++) {
        double sign = (i & 1) ? -1.0 : 1.0;
        s.l[i] = (float)(amp * sign * (0.6 + 0.4 * noise()));
        s.r[i] = (float)(amp * (0.6 + 0.4 * noise()));
    }
}

/* Run the limiter over `in` with host blocks of `block` frames (block <= 0: pseudo-random sizes). */
static void run(const Stereo in, Stereo out, double fs, int block) {
    Limiter lim;
    memset(&lim, 0, sizeof lim);
    limiter_init(&lim, (float)fs);
    uint32_t r = 99;
    for (int pos = 0; pos < in.n;) {
        int c = block;
        if (block <= 0) { r = r * 1103515245u + 12345u; c = 1 + (int)((r >> 16) % 2000); }
        if (c > in.n - pos) c = in.n - pos;
        limiter_process(&lim, in.l + pos, in.r + pos, out.l + pos, out.r + pos, c);
        pos += c;
    }
    limiter_free(&lim);
}

static double peak_abs(const Stereo s, int from, int to) {
    double p = 0;
    for (int i = from; i < to; i++) { p = fmax(p, fabs(s.l[i])); p = fmax(p, fabs(s.r[i])); }
    return p;
}
static double rms(const float *x, int from, int to) {
    double a = 0;
    for (int i = from; i < to; i++) a += (double)x[i] * x[i];
    return sqrt(a / (to - from));
}
static int all_finite(const Stereo s) {
    for (int i = 0; i < s.n; i++) if (!isfinite(s.l[i]) || !isfinite(s.r[i])) return 0;
    return 1;
}
static int identical(const Stereo a, const Stereo b) {
    return memcmp(a.l, b.l, sizeof(float) * a.n) == 0 && memcmp(a.r, b.r, sizeof(float) * a.n) == 0;
}

/* reconstructed (8x band-limited interpolation) peak of a channel, for the true-peak check */
static double true_peak(const float *x, int from, int to) {
    const int os = 8, half = 64;
    double p = 0;
    for (int i = from + half; i < to - half; i++) {
        for (int k = 0; k < os; k++) {
            double t = (double)k / os, acc = 0;
            for (int j = -half + 1; j <= half; j++) {
                double d = t - j, w = 0.5 + 0.5 * cos(PI * d / half);
                acc += x[i + j] * (d == 0 ? 1.0 : sin(PI * d) / (PI * d)) * w;
            }
            p = fmax(p, fabs(acc));
        }
    }
    return p;
}

static void test_rate(double fs, int expected_latency) {
    printf("\n-- %.1f kHz --\n", fs / 1000);
    Limiter probe;
    memset(&probe, 0, sizeof probe);
    limiter_init(&probe, (float)fs);
    int lat = limiter_latency(&probe);
    limiter_free(&probe);
    CHECK(lat == expected_latency, "latency %d frames (%.2f ms), expected %d", lat, lat * 1000.0 / fs, expected_latency);
    CHECK(lat * 1000.0 / fs >= 2.5 && lat * 1000.0 / fs <= 5.5, "latency within 2.5-5.5 ms");

    const int n = (int)(3 * fs);
    Stereo in = alloc_stereo(n), out = alloc_stereo(n), out2 = alloc_stereo(n);

    /* quiet signal: exact unity gain, exactly `lat` frames late */
    gen_sine(in, fs, 1000, 0.1); /* -20 dBFS */
    run(in, out, fs, 480);
    int exact = 1;
    for (int i = 0; i < n; i++) {
        float want = i >= lat ? in.l[i - lat] : 0.0f;
        if (out.l[i] != want || out.r[i] != want) { exact = 0; break; }
    }
    CHECK(exact, "-20 dBFS 1 kHz sine passes bit-exact (unity gain, delayed by the latency)");

    /* +9 dBFS 7.7 kHz sine: limited to just under the ceiling */
    gen_sine(in, fs, 7700, 2.818);
    run(in, out, fs, 480);
    double pk = peak_abs(out, 0, n), amp = rms(out.l, n / 2, n) * sqrt(2.0);
    CHECK(pk <= CEILING, "+9 dBFS 7.7 kHz: peak %.6f <= ceiling", pk);
    CHECK(amp > 0.9, "+9 dBFS 7.7 kHz: steady-state amplitude %.4f (limited, not attenuated)", amp);

    /* +6 dBFS 80 Hz bass */
    gen_sine(in, fs, 80, 2.0);
    run(in, out, fs, 480);
    pk = peak_abs(out, 0, n);
    CHECK(pk <= CEILING, "+6 dBFS 80 Hz: peak %.6f <= ceiling", pk);

    /* loud stereo noise bursts (+9 dB) separated by quiet passages */
    gen_bursts(in, fs, 2.8);
    run(in, out, fs, 480);
    pk = peak_abs(out, 0, n);
    CHECK(pk <= CEILING && all_finite(out), "noise bursts: peak %.6f <= ceiling, all finite", pk);
    {   /* during a burst the level is held near full scale; 500 ms after it the gain is back to unity */
        int a = (int)(1.1 * fs) + lat, b = (int)(1.3 * fs) + lat;
        double loud = rms(out.l, a, b);
        a = (int)(1.7 * fs) + lat, b = (int)(1.95 * fs) + lat;
        double g = rms(out.l, a, b) / rms(in.l, a - lat, b - lat);
        CHECK(loud > 0.2 && loud < 0.7, "noise burst limited to %.2f dBFS RMS", 20 * log10(loud));
        CHECK(g > 0.995, "gain recovered 400 ms after a burst (%.3f dB)", 20 * log10(g));
    }

    /* block-size invariance */
    run(in, out, fs, 1);
    const int sizes[] = { 7, 64, 441, 480, 1000, 4096, 0 };
    for (unsigned k = 0; k < sizeof sizes / sizeof *sizes; k++) {
        run(in, out2, fs, sizes[k]);
        if (sizes[k]) CHECK(identical(out, out2), "host block %4d gives bit-identical output to block 1", sizes[k]);
        else CHECK(identical(out, out2), "random host block sizes give bit-identical output");
    }

    /* in-place processing equals out-of-place */
    {
        Stereo work = alloc_stereo(n);
        memcpy(work.l, in.l, sizeof(float) * n);
        memcpy(work.r, in.r, sizeof(float) * n);
        run(work, work, fs, 480);
        CHECK(identical(out, work), "in-place processing (in == out buffers) is identical");
        free_stereo(work);
    }

    /* reset gives the same result as a fresh instance */
    {
        Limiter lim;
        memset(&lim, 0, sizeof lim);
        limiter_init(&lim, (float)fs);
        limiter_process(&lim, in.l, in.r, out2.l, out2.r, n / 2);
        limiter_reset(&lim);
        limiter_process(&lim, in.l, in.r, out2.l, out2.r, n);
        limiter_free(&lim);
        CHECK(identical(out, out2), "limiter_reset restores the initial state");
    }

    /* non-finite input */
    gen_bursts(in, fs, 2.8);
    for (int i = 1000; i < n; i += 9973) { in.l[i] = NAN; in.r[i + 1] = INFINITY; in.l[i + 2] = -INFINITY; }
    run(in, out, fs, 333);
    CHECK(all_finite(out) && peak_abs(out, 0, n) <= CEILING, "NaN/Inf input: output finite and under the ceiling");

    /* extreme input */
    for (int i = 0; i < n; i++) { in.l[i] = (i & 1) ? 1e30f : -1e30f; in.r[i] = (float)(1e6 * noise()); }
    run(in, out, fs, 512);
    CHECK(all_finite(out) && peak_abs(out, 0, n) <= CEILING, "+600 dBFS input: output finite and under the ceiling");

    free_stereo(in);
    free_stereo(out);
    free_stereo(out2);
}

int main(void) {
    /* expected latency: round(4.3 ms * fs), plus the 32-frame interpolator delay up to 96 kHz */
    test_rate(44100, 190 + 32);
    test_rate(48000, 206 + 32);
    test_rate(96000, 413 + 32);
    test_rate(192000, 826);

    printf("\n-- true-peak side chain (48 kHz) --\n");
    {
        const int n = 48000;
        Stereo in = alloc_stereo(n), out = alloc_stereo(n);
        gen_hf_noise(in, 1.5);
        run(in, out, 48000, 480);
        double sp = peak_abs(out, 0, n), tp = fmax(true_peak(out.l, 4000, n), true_peak(out.r, 4000, n));
        CHECK(sp <= CEILING, "near-Nyquist noise: sample peak %.5f <= ceiling", sp);
        /* a 4x side chain can under-read content right at Nyquist by a few tenths of a dB */
        CHECK(tp <= 1.06, "near-Nyquist noise: reconstructed peak %.5f (%+.2f dBTP) within 0.5 dB of the ceiling", tp, 20 * log10(tp));
        free_stereo(in);
        free_stereo(out);
    }

    printf("\n-- lifecycle --\n");
    {
        Limiter lim;
        memset(&lim, 0, sizeof lim);
        const float rates[] = { 48000, 96000, 44100, 192000, 0, -1, NAN, 48000 };
        float l[256] = { 0 }, r[256] = { 0 };
        int ok = 1;
        for (unsigned k = 0; k < sizeof rates / sizeof *rates; k++) { /* re-init without free, like a host changing rates */
            limiter_init(&lim, rates[k]);
            limiter_process(&lim, l, r, l, r, 256);
            if (!lim.ok || limiter_latency(&lim) <= 0) ok = 0;
        }
        limiter_free(&lim);
        limiter_free(&lim); /* double free is harmless */
        CHECK(ok, "repeated limiter_init at changing/invalid rates, then limiter_free");
    }

    printf(failures ? "\n%d FAILED\n" : "\nall passed\n", failures);
    return failures != 0;
}
