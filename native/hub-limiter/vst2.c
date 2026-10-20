/*
 * Hub limiter: a stereo VST 2.4 effect hosted by Equalizer APO after the EQ (see limiter.h for the DSP).
 * The plugin ABI is declared here directly (no Steinberg SDK). It has no parameters, so Equalizer APO's config
 * only needs "VSTPlugin: Library ...".
 *
 * Part of Hub. Copyright (c) 2026 Hub contributors. GPL-3.0-or-later (see LICENSE).
 */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "limiter.h"

#define EXPORT __declspec(dllexport)

typedef struct AEffect AEffect;
typedef intptr_t (*HostCallback)(AEffect *, int32_t opcode, int32_t index, intptr_t value, void *ptr, float opt);

struct AEffect {
    int32_t magic;
    intptr_t (*dispatcher)(AEffect *, int32_t opcode, int32_t index, intptr_t value, void *ptr, float opt);
    void (*process)(AEffect *, float **in, float **out, int32_t frames);
    void (*setParameter)(AEffect *, int32_t index, float value);
    float (*getParameter)(AEffect *, int32_t index);
    int32_t numPrograms, numParams, numInputs, numOutputs;
    int32_t flags;
    intptr_t reserved1, reserved2;
    int32_t initialDelay;
    int32_t realQualities, offQualities;
    float ioRatio;
    void *object, *user;
    int32_t uniqueID, version;
    void (*processReplacing)(AEffect *, float **in, float **out, int32_t frames);
    void (*processDoubleReplacing)(AEffect *, double **in, double **out, int32_t frames);
    char future[56];
};

enum {
    effOpen = 0, effClose = 1, effGetParamLabel = 6, effGetParamDisplay = 7, effGetParamName = 8,
    effSetSampleRate = 10, effSetBlockSize = 11, effMainsChanged = 12,
    effGetEffectName = 45, effGetVendorString = 47, effGetProductString = 48, effGetVendorVersion = 49,
    effCanDo = 51, effGetVstVersion = 58,
};
enum { effFlagsCanReplacing = 1 << 4 };
#define FOURCC(a, b, c, d) (((int32_t)(a) << 24) | ((int32_t)(b) << 16) | ((int32_t)(c) << 8) | (int32_t)(d))

typedef struct {
    AEffect effect;
    Limiter limiter;
    float sampleRate;
} Plugin;

static Plugin *self(AEffect *e) { return (Plugin *)e->object; }

static void copy_string(void *dst, const char *src, size_t max) {
    strncpy((char *)dst, src, max - 1);
    ((char *)dst)[max - 1] = 0;
}

static intptr_t dispatcher(AEffect *e, int32_t opcode, int32_t index, intptr_t value, void *ptr, float opt) {
    Plugin *p = self(e);
    (void)index; (void)value;
    switch (opcode) {
    case effClose:
        limiter_free(&p->limiter);
        free(p);
        return 1;
    case effSetSampleRate:
        p->sampleRate = opt;
        limiter_init(&p->limiter, opt);
        e->initialDelay = limiter_latency(&p->limiter);
        return 1;
    case effMainsChanged:
        if (value) limiter_reset(&p->limiter);
        return 1;
    case effGetEffectName: copy_string(ptr, "Hub Limiter", 32); return 1;
    case effGetProductString: copy_string(ptr, "Hub Limiter", 64); return 1;
    case effGetVendorString: copy_string(ptr, "Hub", 64); return 1;
    case effGetVendorVersion: return 1000;
    case effGetVstVersion: return 2400;
    case effCanDo: return 0;
    default: return 0;
    }
}

static void process_replacing(AEffect *e, float **in, float **out, int32_t frames) {
    limiter_process(&self(e)->limiter, in[0], in[1], out[0], out[1], frames);
}

static void set_parameter(AEffect *e, int32_t i, float v) { (void)e; (void)i; (void)v; }
static float get_parameter(AEffect *e, int32_t i) { (void)e; (void)i; return 0; }

EXPORT AEffect *VSTPluginMain(HostCallback host) {
    if (!host) return 0;
    Plugin *p = (Plugin *)calloc(1, sizeof(Plugin));
    if (!p) return 0;
    AEffect *e = &p->effect;
    e->magic = FOURCC('V', 's', 't', 'P');
    e->dispatcher = dispatcher;
    e->setParameter = set_parameter;
    e->getParameter = get_parameter;
    e->processReplacing = process_replacing;
    e->numPrograms = 0;
    e->numParams = 0;
    e->numInputs = 2;
    e->numOutputs = 2;
    e->flags = effFlagsCanReplacing;
    e->ioRatio = 1.0f;
    e->object = p;
    e->uniqueID = FOURCC('H', 'u', 'b', 'L');
    e->version = 1000;
    p->sampleRate = 48000.0f;
    limiter_init(&p->limiter, p->sampleRate);
    e->initialDelay = limiter_latency(&p->limiter);
    return e;
}
