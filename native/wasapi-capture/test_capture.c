// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include "capture.c"
#undef NDEBUG
#include <assert.h>
#include <stdio.h>

static DWORD next_result = 32;
static DWORD WINAPI fake_mix(DWORD handle, void *buffer, DWORD length) {
    assert(handle == 7);
    memset(buffer, 0x5a, length);
    return next_result;
}

static _Atomic int finished;
static DWORD WINAPI producer(void *user) {
    unsigned char pcm[32];
    for (int i = 0; i < 100000; i++)
        assert(g0_capture_callback(pcm, sizeof(pcm), user) == sizeof(pcm));
    atomic_store_explicit(&finished, 1, memory_order_release);
    return 0;
}

int main(void) {
    Capture *c = g0_capture_create(fake_mix);
    assert(c && g0_capture_configure(c, 48000, 2));
    unsigned char pcm[MAX_BYTES + 8], copy[MAX_BYTES];
    CaptureInfo info;
    assert(g0_capture_callback(pcm, 32, c) == 0);
    g0_capture_set_mixer(c, 7);
    assert(g0_capture_callback(pcm, 32, c) == 32);
    assert(g0_capture_read(c, copy, sizeof(copy), &info) == 0);
    g0_capture_enable(c, 1);
    for (unsigned i = 0; i < SLOT_COUNT + 1; i++)
        assert(g0_capture_callback(pcm, 32, c) == 32);
    assert(g0_capture_dropped_frames(c) == 4);
    assert(g0_capture_read(c, copy, 1, &info) == -1);
    for (unsigned i = 0; i < SLOT_COUNT; i++) {
        assert(g0_capture_read(c, copy, sizeof(copy), &info) == 1);
        assert(info.sample_position == (i + 1) * 4);
        assert(info.frequency == 48000 && info.channels == 2 && info.qpc > 0);
        for (unsigned j = 0; j < info.length; j++) assert(copy[j] == 0x5a);
    }
    assert(g0_capture_callback(pcm, 32, c) == 32);
    assert(g0_capture_read(c, copy, sizeof(copy), &info) == 1);
    assert(info.sample_position == (SLOT_COUNT + 2) * 4); // Explicit gap after dropped block.
    next_result = (DWORD)-1;
    assert(g0_capture_callback(pcm, 32, c) == 0);
    next_result = MAX_BYTES + 8;
    assert(g0_capture_callback(pcm, sizeof(pcm), c) == sizeof(pcm));
    assert(g0_capture_read(c, copy, sizeof(copy), &info) == 0);
    next_result = 32;
    assert(g0_capture_configure(c, 48000, 2));
    // Exercise wrapping unsigned indices without billions of callbacks.
    atomic_store(&c->read_index, UINT32_MAX - 2);
    atomic_store(&c->write_index, UINT32_MAX - 2);
    HANDLE thread = CreateThread(NULL, 0, producer, c, 0, NULL);
    assert(thread);
    uint64_t previous = 0, consumed = 0;
    for (;;) {
        int result = g0_capture_read(c, copy, sizeof(copy), &info);
        if (result == 1) {
            assert(!consumed || info.sample_position > previous);
            previous = info.sample_position;
            consumed += info.length / 8;
            for (unsigned j = 0; j < info.length; j++) assert(copy[j] == 0x5a);
        } else if (atomic_load_explicit(&finished, memory_order_acquire)) {
            if (atomic_load(&c->read_index) == atomic_load(&c->write_index)) break;
        }
    }
    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
    assert(consumed + g0_capture_dropped_frames(c) == 400000);
    g0_capture_destroy(c);
    puts("PASS: passthrough, overflow, gaps, short reads, errors, wraparound, concurrent SPSC (100000 blocks)");
}
