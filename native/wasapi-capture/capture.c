// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include <windows.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define API __declspec(dllexport)
#define SLOT_COUNT 32u
#define MAX_BYTES 65536u

typedef DWORD (WINAPI *GetData)(DWORD, void *, DWORD);
typedef struct {
    uint64_t sample_position;
    int64_t qpc;
    uint32_t length;
    uint32_t frequency;
    uint32_t channels;
    uint32_t reserved;
} CaptureInfo;
typedef struct {
    CaptureInfo info;
    unsigned char pcm[MAX_BYTES];
} Slot;
typedef struct {
    GetData get_data;
    _Atomic uint32_t mixer;
    _Atomic uint32_t enabled;
    _Atomic uint32_t write_index;
    _Atomic uint32_t read_index;
    _Atomic uint64_t dropped_frames;
    uint64_t position; // Producer only, includes dropped and disabled intervals.
    uint32_t frequency;
    uint32_t channels;
    Slot slots[SLOT_COUNT];
} Capture;

_Static_assert(ATOMIC_INT_LOCK_FREE == 2, "Realtime indices must be lock-free");
_Static_assert(ATOMIC_LLONG_LOCK_FREE == 2, "Realtime counters must be lock-free");
_Static_assert(sizeof(CaptureInfo) == 32, "CaptureInfo ABI mismatch");

API uint32_t g0_capture_abi(void) { return 1; }

// Called outside the callback. Touch every page before playback starts.
API Capture *g0_capture_create(GetData get_data) {
    if (!get_data) return NULL;
    Capture *c = calloc(1, sizeof(*c));
    if (!c) return NULL;
    volatile unsigned char *bytes = (volatile unsigned char *)c;
    for (size_t i = 0; i < sizeof(*c); i += 4096) bytes[i] = 0;
    atomic_init(&c->mixer, 0);
    atomic_init(&c->enabled, 0);
    atomic_init(&c->write_index, 0);
    atomic_init(&c->read_index, 0);
    atomic_init(&c->dropped_frames, 0);
    c->get_data = get_data;
    return c;
}

// Configure and destroy require a stopped/joined WASAPI producer and no active reader.
API void g0_capture_destroy(Capture *c) { free(c); }
API int g0_capture_configure(Capture *c, uint32_t frequency, uint32_t channels) {
    if (!c || !frequency || !channels || channels > 32) return 0;
    c->frequency = frequency;
    c->channels = channels;
    c->position = 0;
    atomic_store(&c->write_index, 0);
    atomic_store(&c->read_index, 0);
    atomic_store(&c->dropped_frames, 0);
    return 1;
}
API void g0_capture_set_mixer(Capture *c, uint32_t mixer) {
    atomic_store_explicit(&c->mixer, mixer, memory_order_release);
}
API void g0_capture_enable(Capture *c, uint32_t enabled) {
    atomic_store_explicit(&c->enabled, enabled != 0, memory_order_release);
}
API uint64_t g0_capture_dropped_frames(Capture *c) {
    return atomic_load_explicit(&c->dropped_frames, memory_order_relaxed);
}

// Exactly one native producer. No CLR, allocation, logging, locks or consumer waits.
API DWORD WINAPI g0_capture_callback(void *buffer, DWORD length, void *user) {
    Capture *c = user;
    DWORD mixer = atomic_load_explicit(&c->mixer, memory_order_acquire);
    if (!mixer) return 0;
    DWORD result = c->get_data(mixer, buffer, length);
    if (result == (DWORD)-1 || result > length) return 0;
    uint32_t stride = c->channels * sizeof(float);
    if (!stride || !result) return result;
    uint64_t start = c->position;
    c->position += result / stride;
    if (!atomic_load_explicit(&c->enabled, memory_order_acquire)) return result;

    uint32_t write = atomic_load_explicit(&c->write_index, memory_order_relaxed);
    uint32_t read = atomic_load_explicit(&c->read_index, memory_order_acquire);
    if (write - read >= SLOT_COUNT || result > MAX_BYTES || result % stride != 0) {
        atomic_fetch_add_explicit(&c->dropped_frames, result / stride, memory_order_relaxed);
        return result; // Preserve the exact device output, even on capture overflow.
    }
    Slot *slot = &c->slots[write % SLOT_COUNT];
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    slot->info = (CaptureInfo){start, now.QuadPart, result, c->frequency, c->channels, 0};
    memcpy(slot->pcm, buffer, result);
    atomic_store_explicit(&c->write_index, write + 1, memory_order_release);
    return result;
}

// Exactly one consumer. -1 means destination too small; the block remains queued.
API int g0_capture_read(Capture *c, void *destination, uint32_t capacity, CaptureInfo *info) {
    if (!c || !destination || !info) return -1;
    uint32_t read = atomic_load_explicit(&c->read_index, memory_order_relaxed);
    uint32_t write = atomic_load_explicit(&c->write_index, memory_order_acquire);
    if (read == write) return 0;
    Slot *slot = &c->slots[read % SLOT_COUNT];
    *info = slot->info;
    if (capacity < slot->info.length) return -1;
    memcpy(destination, slot->pcm, slot->info.length);
    atomic_store_explicit(&c->read_index, read + 1, memory_order_release);
    return 1;
}
