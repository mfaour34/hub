/* Plain C API over the limiter for Hub's own audio engine (no VST host involved). */
#include <stdint.h>
#include <stdlib.h>
#include "limiter.h"

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

/* threshold: linear, 1.0 = 0 dBFS; the output never exceeds the limiter's ceiling x threshold. */
EXPORT Limiter *hub_limiter_create(float sampleRate, double threshold)
{
    Limiter *l = calloc(1, sizeof *l);
    if (!l) return NULL;
    limiter_init(l, sampleRate);
    if (threshold > 0) l->ceiling *= threshold;
    if (!l->ok) { limiter_free(l); free(l); return NULL; }
    return l;
}

EXPORT void hub_limiter_destroy(Limiter *l)
{
    if (!l) return;
    limiter_free(l);
    free(l);
}

EXPORT void hub_limiter_reset(Limiter *l) { limiter_reset(l); }
EXPORT int32_t hub_limiter_latency(const Limiter *l) { return limiter_latency(l); }

/* In place is allowed: outL may equal inL. */
EXPORT void hub_limiter_process(Limiter *l, const float *inL, const float *inR, float *outL, float *outR, int32_t frames)
{
    limiter_process(l, inL, inR, outL, outR, frames);
}
