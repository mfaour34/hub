/*
 * Hub limiter - stereo-linked, true-peak-aware look-ahead limiter.
 *
 * Original work, Copyright (c) 2026 the Hub authors. Released under the MIT License.
 *
 * Algorithm. A textbook feed-forward look-ahead limiter (see D. Giannoulis, M. Massberg and
 * J. D. Reiss, "Digital Dynamic Range Compressor Design - A Tutorial and Analysis", J. Audio Eng.
 * Soc. 60(6), 2012; U. Zoelzer, "DAFX: Digital Audio Effects", 2nd ed., ch. 4) with a true-peak
 * side chain in the spirit of ITU-R BS.1770-4 Annex 2 and a two-stage release:
 *
 *   1. Side-chain level. For each frame the stereo-linked level is the largest of |L|, |R| and the
 *      inter-sample values that follow them, estimated by a polyphase Kaiser-windowed-sinc
 *      interpolator (4x up to 48 kHz, 2x up to 96 kHz, none above). Limiting on this estimate
 *      keeps the reconstructed waveform, not only the samples, under the ceiling.
 *   2. Look-ahead and hold. The audio is delayed by the look-ahead D (plus the interpolator's centre
 *      delay). A sliding-window maximum of the level over the last D + H frames (H = hold time)
 *      gives the required gain  g = min(1, ceiling / peak).
 *   3. Attack. g is smoothed with a moving average of length D. Every sample in that window already
 *      contains the peak, so the gain ramps down over the look-ahead and reaches the required value
 *      exactly when the peak leaves the delay line - no overshoot, no clipping.
 *   4. Release, fast stage: reductions are followed immediately, recovery is a short one-pole.
 *      Release, slow stage: the fast-stage gain is averaged over a long window and the applied gain
 *      is the lower of the two. Isolated peaks recover quickly; after sustained limiting the gain
 *      comes back as a gentle, roughly linear ramp (a program-dependent, "dual time constant"
 *      release). Both stages can only lower the gain, so the attack stage's guarantee is kept.
 *   5. A final hard clamp at the ceiling catches rounding residue; non-finite input is muted.
 *
 * The plugin has no parameters. The constants in limiter.c were tuned by measurement for loud game
 * audio after a boosting EQ: transparent below full scale, fast on bursts. Processing is strictly frame by
 * frame, so the output does not depend on the host's block size.
 */
#ifndef HUB_LIMITER_H
#define HUB_LIMITER_H

#define LIMITER_MAX_PHASES 3 /* inter-sample points per frame (4x oversampling) */
#define LIMITER_TAPS 64      /* interpolator taps per phase */

typedef struct {
    /* configuration (derived from the sample rate by limiter_init) */
    float sample_rate;
    int ok;              /* buffers allocated */
    int lookahead;       /* D, frames */
    int window;          /* peak window D + H, frames */
    int latency;         /* audio-path delay, frames */
    int phases;          /* inter-sample points evaluated per frame (0..LIMITER_MAX_PHASES) */
    int block_len;       /* slow stage: sub-block length, frames (1 ms) */
    int ring_len;        /* slow stage: averaging length, sub-blocks */
    double ceiling;      /* linear output ceiling */
    double release_coef; /* fast-stage one-pole release coefficient per frame */
    float interp[LIMITER_MAX_PHASES][LIMITER_TAPS];

    /* buffers (heap, sized for the sample rate) */
    float *delay;        /* interleaved stereo delay line, `latency` frames */
    double *win_val;     /* sliding-maximum deque, window + 1 entries */
    long long *win_idx;
    double *avg_buf;     /* attack moving-average history, D entries */
    double *ring;        /* slow-stage sub-block means, ring_len entries */

    /* running state */
    long long frame;
    float hist[2][2 * LIMITER_TAPS]; /* interpolator input, mirrored ring */
    int hist_pos;
    int delay_pos;
    int win_head, win_count;
    int avg_pos;
    double avg_sum;
    double fast_gain;
    double block_acc;
    int block_count;
    int ring_pos;
    double ring_sum;
    double slow_gain, slow_step;
} Limiter;

/* (Re)configure for a sample rate and clear the state. Safe to call repeatedly; a zero-initialised
 * Limiter is a valid first argument. */
void limiter_init(Limiter *lim, float sample_rate);
/* Clear the audio and side-chain state, keeping the configuration. */
void limiter_reset(Limiter *lim);
/* Processing latency in frames. */
int limiter_latency(const Limiter *lim);
/* Process n stereo frames. Input and output buffers may alias. */
void limiter_process(Limiter *lim, const float *in_l, const float *in_r, float *out_l, float *out_r, int n);
/* Free the buffers (the Limiter can be re-initialised afterwards). */
void limiter_free(Limiter *lim);

#endif
