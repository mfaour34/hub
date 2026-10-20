/*
 * Hub limiter - stereo-linked, true-peak-aware look-ahead limiter.
 * See limiter.h for the algorithm and references.
 *
 * Original work, Copyright (c) 2026 the Hub authors. Released under the MIT License.
 */
#include "limiter.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

/* Tuning. Chosen by measurement on Hub's A/B test signal (loud noise bursts after a boosting EQ, the
 * recovery after them, and a loud sine sweep).
 * The overrides exist only for offline tuning builds. */
#ifndef LIMITER_CEILING
#define LIMITER_CEILING 0.99997 /* linear, about -0.0003 dBFS */
#endif
#ifndef LIMITER_LOOKAHEAD_MS
#define LIMITER_LOOKAHEAD_MS 4.3 /* attack ramp / look-ahead */
#endif
#ifndef LIMITER_HOLD_MS
#define LIMITER_HOLD_MS 50.0 /* peak hold after the look-ahead window */
#endif
#ifndef LIMITER_RELEASE_MS
#define LIMITER_RELEASE_MS 5.0 /* fast-stage one-pole time constant */
#endif
#ifndef LIMITER_AVERAGE_MS
#define LIMITER_AVERAGE_MS 300.0 /* slow-stage averaging window */
#endif
#define KAISER_BETA 5.0

static const double PI = 3.14159265358979323846;

/* Zeroth-order modified Bessel function of the first kind (power series). */
static double bessel_i0(double x) {
    double sum = 1.0, term = 1.0, q = x * x / 4.0;
    for (int k = 1; k < 100; k++) {
        term *= q / ((double)k * k);
        sum += term;
        if (term < 1e-15 * sum) break;
    }
    return sum;
}

/* Kaiser-windowed sinc taps evaluating the waveform at fraction p/(phases+1) of a frame after the
 * centre sample. Tap j multiplies the sample at offset j - (TAPS/2 - 1) from the centre. */
static void design_interpolator(Limiter *lim) {
    const int half = LIMITER_TAPS / 2;
    for (int p = 0; p < lim->phases; p++) {
        double frac = (double)(p + 1) / (lim->phases + 1), taps[LIMITER_TAPS], sum = 0.0;
        for (int j = 0; j < LIMITER_TAPS; j++) {
            double t = frac - (double)(j - (half - 1));
            double r = t / half;
            double w = r * r < 1.0 ? bessel_i0(KAISER_BETA * sqrt(1.0 - r * r)) / bessel_i0(KAISER_BETA) : 0.0;
            taps[j] = sin(PI * t) / (PI * t) * w;
            sum += taps[j];
        }
        for (int j = 0; j < LIMITER_TAPS; j++) lim->interp[p][j] = (float)(taps[j] / sum); /* unity DC gain */
    }
}

void limiter_free(Limiter *lim) {
    free(lim->delay);
    free(lim->win_val);
    free(lim->win_idx);
    free(lim->avg_buf);
    free(lim->ring);
    lim->delay = NULL;
    lim->win_val = NULL;
    lim->win_idx = NULL;
    lim->avg_buf = NULL;
    lim->ring = NULL;
    lim->ok = 0;
}

void limiter_reset(Limiter *lim) {
    memset(lim->hist, 0, sizeof lim->hist);
    lim->frame = 0;
    lim->hist_pos = 0;
    lim->delay_pos = 0;
    lim->win_head = lim->win_count = 0;
    lim->avg_pos = 0;
    lim->avg_sum = lim->lookahead;
    lim->fast_gain = 1.0;
    lim->block_acc = 0.0;
    lim->block_count = 0;
    lim->ring_pos = 0;
    lim->ring_sum = lim->ring_len;
    lim->slow_gain = 1.0;
    lim->slow_step = 0.0;
    if (!lim->ok) return;
    memset(lim->delay, 0, sizeof(float) * 2 * (size_t)lim->latency);
    for (int i = 0; i < lim->lookahead; i++) lim->avg_buf[i] = 1.0; /* history = silence = gain 1 */
    for (int i = 0; i < lim->ring_len; i++) lim->ring[i] = 1.0;
}

static int frames(double ms, double rate) { return (int)lround(ms * 0.001 * rate); }

void limiter_init(Limiter *lim, float sample_rate) {
    limiter_free(lim);
    if (!(sample_rate >= 8000.0f && sample_rate <= 1536000.0f)) sample_rate = 48000.0f;
    lim->sample_rate = sample_rate;
    lim->phases = sample_rate <= 50000.0f ? 3 : (sample_rate <= 100000.0f ? 1 : 0);
    lim->lookahead = frames(LIMITER_LOOKAHEAD_MS, sample_rate);
    if (lim->lookahead < 1) lim->lookahead = 1;
    lim->window = lim->lookahead + frames(LIMITER_HOLD_MS, sample_rate);
    lim->latency = lim->lookahead + (lim->phases ? LIMITER_TAPS / 2 : 0); /* + interpolator centre delay */
    lim->block_len = frames(1.0, sample_rate);
    lim->ring_len = (int)lround(LIMITER_AVERAGE_MS);
    if (lim->ring_len < 1) lim->ring_len = 1;
    lim->ceiling = LIMITER_CEILING;
    lim->release_coef = exp(-1.0 / (LIMITER_RELEASE_MS * 0.001 * sample_rate));
    design_interpolator(lim);

    lim->delay = malloc(sizeof(float) * 2 * (size_t)lim->latency);
    lim->win_val = malloc(sizeof(double) * ((size_t)lim->window + 1));
    lim->win_idx = malloc(sizeof(long long) * ((size_t)lim->window + 1));
    lim->avg_buf = malloc(sizeof(double) * (size_t)lim->lookahead);
    lim->ring = malloc(sizeof(double) * (size_t)lim->ring_len);
    lim->ok = lim->delay && lim->win_val && lim->win_idx && lim->avg_buf && lim->ring;
    if (!lim->ok) limiter_free(lim);
    limiter_reset(lim);
}

int limiter_latency(const Limiter *lim) { return lim->ok ? lim->latency : 0; }

/* Push one frame into the interpolator; return the stereo-linked true-peak level of the frame
 * `latency - lookahead` frames back: its own samples and the inter-sample points after them. */
static double side_chain_level(Limiter *lim, float xl, float xr) {
    if (!lim->phases) return fmax(fabs((double)xl), fabs((double)xr));
    const int len = LIMITER_TAPS;
    int pos = lim->hist_pos;
    lim->hist[0][pos] = lim->hist[0][pos + len] = xl;
    lim->hist[1][pos] = lim->hist[1][pos + len] = xr;
    lim->hist_pos = pos + 1 == len ? 0 : pos + 1;
    double level = 0.0;
    for (int c = 0; c < 2; c++) {
        const float *x = &lim->hist[c][lim->hist_pos]; /* oldest .. newest, contiguous */
        double v = fabs((double)x[len / 2 - 1]);
        if (v > level) level = v;
        for (int p = 0; p < lim->phases; p++) {
            const float *h = lim->interp[p];
            float acc = 0.0f;
            for (int j = 0; j < len; j++) acc += h[j] * x[j];
            v = fabs((double)acc);
            if (v > level) level = v;
        }
    }
    return level;
}

/* Maximum of the side-chain level over the last window + 1 frames (monotonic deque). */
static double window_peak(Limiter *lim, double level) {
    const int cap = lim->window + 1;
    const long long now = lim->frame;
    while (lim->win_count > 0) { /* entries not above the new level can never be the maximum again */
        int back = (lim->win_head + lim->win_count - 1) % cap;
        if (lim->win_val[back] > level) break;
        lim->win_count--;
    }
    int slot = (lim->win_head + lim->win_count) % cap;
    lim->win_val[slot] = level;
    lim->win_idx[slot] = now;
    lim->win_count++;
    while (lim->win_idx[lim->win_head] < now - lim->window) { /* expire entries left of the window */
        lim->win_head = (lim->win_head + 1) % cap;
        lim->win_count--;
    }
    return lim->win_val[lim->win_head];
}

/* Moving average over the last D required gains (the attack ramp). */
static double attack_smooth(Limiter *lim, double target) {
    const int d = lim->lookahead;
    lim->avg_sum += target - lim->avg_buf[lim->avg_pos];
    lim->avg_buf[lim->avg_pos] = target;
    if (++lim->avg_pos == d) { /* once per window, re-sum exactly so rounding cannot accumulate */
        double s = 0.0;
        for (int i = 0; i < d; i++) s += lim->avg_buf[i];
        lim->avg_sum = s;
        lim->avg_pos = 0;
    }
    return lim->avg_sum / d;
}

/* Slow stage: average of the fast gain over the last ring_len sub-blocks, recomputed at the end of
 * every 1 ms sub-block and approached linearly over the following sub-block. */
static double slow_stage(Limiter *lim, double fast) {
    lim->block_acc += fast;
    if (++lim->block_count == lim->block_len) {
        double mean = lim->block_acc / lim->block_len;
        lim->ring_sum += mean - lim->ring[lim->ring_pos];
        lim->ring[lim->ring_pos] = mean;
        if (++lim->ring_pos == lim->ring_len) {
            double s = 0.0;
            for (int i = 0; i < lim->ring_len; i++) s += lim->ring[i];
            lim->ring_sum = s;
            lim->ring_pos = 0;
        }
        lim->slow_step = (lim->ring_sum / lim->ring_len - lim->slow_gain) / lim->block_len;
        lim->block_acc = 0.0;
        lim->block_count = 0;
    }
    lim->slow_gain += lim->slow_step;
    return lim->slow_gain;
}

static float clamp(float v, float c) { return v > c ? c : (v < -c ? -c : v); }

void limiter_process(Limiter *lim, const float *in_l, const float *in_r, float *out_l, float *out_r, int n) {
    float ceil_f = (float)lim->ceiling;
    if ((double)ceil_f > lim->ceiling) ceil_f = nextafterf(ceil_f, 0.0f); /* never round above it */
    if (!lim->ok) { /* allocation failed: pass through, still never above the ceiling */
        for (int i = 0; i < n; i++) {
            float l = in_l[i], r = in_r[i];
            out_l[i] = isfinite(l) ? clamp(l, ceil_f) : 0.0f;
            out_r[i] = isfinite(r) ? clamp(r, ceil_f) : 0.0f;
        }
        return;
    }
    for (int i = 0; i < n; i++) {
        float xl = in_l[i], xr = in_r[i];
        if (!isfinite(xl)) xl = 0.0f;
        if (!isfinite(xr)) xr = 0.0f;

        /* side chain */
        double peak = window_peak(lim, side_chain_level(lim, xl, xr));
        double required = peak > lim->ceiling ? lim->ceiling / peak : 1.0;
        double smooth = attack_smooth(lim, required);
        double fast = lim->fast_gain;
        fast = smooth < fast ? smooth : smooth + lim->release_coef * (fast - smooth);
        lim->fast_gain = fast;
        double slow = slow_stage(lim, fast);
        double g = slow < fast ? slow : fast;
        lim->frame++;

        /* audio path: take the frame that entered `latency` frames ago, store the new one */
        float *slot = &lim->delay[2 * lim->delay_pos];
        float dl = slot[0], dr = slot[1];
        slot[0] = xl;
        slot[1] = xr;
        if (++lim->delay_pos == lim->latency) lim->delay_pos = 0;

        out_l[i] = clamp((float)(dl * g), ceil_f);
        out_r[i] = clamp((float)(dr * g), ceil_f);
    }
}
