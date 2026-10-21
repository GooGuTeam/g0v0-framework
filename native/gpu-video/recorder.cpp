// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#include "encoder.cpp"
#include "pooled_converter.h"

struct Recording {
    Encoder *encoder = nullptr;
    bool ownsCom = false;
    PooledConverter converter;
    bool converterReady = false;
    ID3D11Device *device = nullptr;
    ID3D11DeviceContext *context = nullptr;
    ID3D11Query *query = nullptr;
    ID3D11Texture2D *textures[16] = {};
    UINT64 accepted = 0, poolDrops = 0, cadenceDrops = 0;
    FrameCompletion *tickets[16] = {};
};

API HRESULT g0_record_prepare(ID3D11Device *device) {
    if (!device) return E_INVALIDARG;
    ID3D10Multithread *threading = nullptr;
    HRESULT hr = device->QueryInterface(__uuidof(ID3D10Multithread), (void **)&threading);
    if (SUCCEEDED(hr)) { threading->SetMultithreadProtected(TRUE); threading->Release(); }
    return hr;
}
static HRESULT open_recording(ID3D11Device *device, const wchar_t *path, UINT width, UINT height,
    UINT rate, UINT channels, Recording **output, PacketCallback callback = nullptr, void *context = nullptr,
    UINT fps = 60, UINT bitrate = 12000000, UINT profile = 0) {
    if (!output) return E_POINTER;
    *output = nullptr;
    if (!device || (!path && !callback) || !width || !height || (width & 1) || (height & 1)) return E_INVALIDARG;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool ownsCom = SUCCEEDED(hr);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return hr;
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) { if (ownsCom) CoUninitialize(); return hr; }
    auto r = new(std::nothrow) Recording();
    if (!r) { MFShutdown(); if (ownsCom) CoUninitialize(); return E_OUTOFMEMORY; }
    r->ownsCom = ownsCom;
    r->device = device; device->AddRef(); device->GetImmediateContext(&r->context);
    D3D11_QUERY_DESC query = {D3D11_QUERY_EVENT, 0};
    hr = device->CreateQuery(&query, &r->query);
    CHECK(open_video(device, path, width, height, fps, bitrate, rate, channels, &r->encoder, callback, context, profile));
    if (FAILED(hr)) {
        release(r->query); release(r->context); release(r->device); delete r;
        MFShutdown(); if (ownsCom) CoUninitialize(); return hr;
    }
    *output = r; return S_OK;
}
API HRESULT g0_record_open(ID3D11Device *device, const wchar_t *path, UINT width, UINT height,
    UINT rate, UINT channels, Recording **output) {
    return open_recording(device, path, width, height, rate, channels, output);
}
API HRESULT g0_stream_open(ID3D11Device *device, UINT width, UINT height, UINT rate, UINT channels,
    PacketCallback callback, void *context, Recording **output) {
    if (!callback) return E_INVALIDARG;
    return open_recording(device, nullptr, width, height, rate, channels, output, callback, context);
}
API HRESULT g0_record_open_configured(ID3D11Device *device, const wchar_t *path, UINT width, UINT height,
    UINT rate, UINT channels, UINT fps, UINT bitrate, UINT profile, Recording **output) {
    return open_recording(device, path, width, height, rate, channels, output, nullptr, nullptr, fps, bitrate, profile);
}
API HRESULT g0_stream_open_configured(ID3D11Device *device, UINT width, UINT height, UINT rate, UINT channels,
    UINT fps, UINT bitrate, UINT profile, PacketCallback callback, void *context, Recording **output) {
    if (!callback) return E_INVALIDARG;
    return open_recording(device, nullptr, width, height, rate, channels, output, callback, context, fps, bitrate, profile);
}
API HRESULT g0_stream_keyframe(Recording *r) { return r ? g0_video_keyframe(r->encoder) : E_INVALIDARG; }

static HRESULT write_record_frame(Recording *r, ID3D11Texture2D *source, LONGLONG time, bool offline, LONGLONG frameIndex) {
    if (!r || !source || time < 0) return E_INVALIDARG;
    int slot = -1;
    const ULONGLONG slotDeadline = GetTickCount64() + 10000;
    do {
    for (int i = 0; i < 16; i++) {
        if (r->tickets[i] && g0_video_frame_complete(r->tickets[i])) {
            g0_video_frame_release(r->tickets[i]); r->tickets[i] = nullptr;
            release(r->textures[i]);
        }
        if (!r->tickets[i]) slot = i;
    }
    if (slot >= 0 || !offline) break;
    if (GetTickCount64() >= slotDeadline) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    Sleep(1);
    } while (true);
    if (slot >= 0) release(r->textures[slot]);
    if (slot < 0) { r->poolDrops++; return S_FALSE; } // Bounded backpressure; never wait for encoder completion.
    ID3D11Device *owner = nullptr;
    source->GetDevice(&owner);
    const bool sameDevice = owner == r->device;
    release(owner);
    if (!sameDevice) return E_INVALIDARG;
    D3D11_TEXTURE2D_DESC desc; source->GetDesc(&desc);
    HRESULT hr = S_OK;
    if (!r->converterReady) {
        hr = r->converter.Initialise(r->device, r->context, desc, r->encoder->width, r->encoder->height);
        if (FAILED(hr)) return hr;
        r->converterReady = true;
    }
    hr = r->converter.Convert(r->device, r->context, source, slot, &r->textures[slot]);
    if (FAILED(hr)) return hr;
    // Source lease is retained by the managed worker until this query completes.
    r->context->End(r->query); r->context->Flush();
    ULONGLONG deadline = GetTickCount64() + 5000;
    while ((hr = r->context->GetData(r->query, nullptr, 0, 0)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
    if (hr == S_FALSE) hr = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    if (FAILED(hr)) return hr;
    // Preserve wall-clock gaps on dropped frames at the configured frame rate.
    LONGLONG index = offline ? frameIndex : time * r->encoder->fps / 10000000;
    if (index < r->encoder->next) { r->cadenceDrops++; release(r->textures[slot]); return S_FALSE; }
    r->encoder->next = index;
    hr = g0_video_write(r->encoder, r->textures[slot], &r->tickets[slot]);
    if (!r->tickets[slot]) release(r->textures[slot]);
    if (SUCCEEDED(hr)) r->accepted++;
    return hr;
}
API HRESULT g0_record_frame(Recording *r, ID3D11Texture2D *source, LONGLONG time) {
    return write_record_frame(r, source, time, false, 0);
}
// Dedicated offline worker only. Explicit indices avoid timestamp quantisation loss.
API HRESULT g0_record_offline_frame(Recording *r, ID3D11Texture2D *source, LONGLONG index) {
    if (!r || index < 0 || index != r->encoder->next) return E_INVALIDARG;
    return write_record_frame(r, source, 0, true, index);
}
API HRESULT g0_record_audio(Recording *r, const float *pcm, UINT frames, LONGLONG position) {
    return g0_video_audio(r->encoder, pcm, frames, position);
}
API void g0_record_stats(Recording *r, UINT64 *accepted, UINT64 *poolDrops, UINT64 *cadenceDrops) {
    *accepted = r->accepted; *poolDrops = r->poolDrops; *cadenceDrops = r->cadenceDrops;
}
API HRESULT g0_record_close(Recording *r) {
    if (!r) return E_INVALIDARG;
    HRESULT hr = g0_video_close(r->encoder);
    for (int i = 0; i < 16; i++) {
        // COM buffer references protect textures even if finalisation failed.
        g0_video_frame_release(r->tickets[i]); release(r->textures[i]);
    }
    const bool ownsCom = r->ownsCom;
    release(r->query); release(r->context); release(r->device); delete r;
    MFShutdown(); if (ownsCom) CoUninitialize(); return hr;
}
