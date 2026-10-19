// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#pragma once
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <atomic>
#include <mutex>
#include <vector>
#include <new>

// The callback borrows its data only for the duration of the call. It must copy
// into a bounded queue and must never perform network I/O on an encoder thread.
using PacketCallback = void (__cdecl *)(void *, UINT, const BYTE *, UINT, LONGLONG, LONGLONG, LONGLONG, UINT);

class PacketSink;
class PacketStream final : public IMFStreamSink, public IMFMediaTypeHandler {
    std::atomic<ULONG> refs{1};
    std::recursive_mutex gate;
    PacketSink *owner;
    DWORD id;
    IMFMediaEventQueue *events = nullptr;
    IMFMediaType *type = nullptr;
    PacketCallback callback;
    void *context;
    bool stopped = false;
public:
    PacketStream(PacketSink *parent, DWORD stream, IMFMediaType *mediaType, PacketCallback cb, void *ctx)
        : owner(parent), id(stream), type(mediaType), callback(cb), context(ctx) { type->AddRef(); }
    HRESULT Initialise() { return MFCreateEventQueue(&events); }
    ~PacketStream() { if (events) events->Release(); if (type) type->Release(); }
    STDMETHODIMP QueryInterface(REFIID iid, void **out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IMFStreamSink) || iid == __uuidof(IMFMediaEventGenerator))
            *out = static_cast<IMFStreamSink *>(this);
        else if (iid == __uuidof(IMFMediaTypeHandler)) *out = static_cast<IMFMediaTypeHandler *>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG count = --refs; if (!count) delete this; return count; }
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent **out) override { return events->GetEvent(flags, out); }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback *cb, IUnknown *state) override { return events->BeginGetEvent(cb, state); }
    STDMETHODIMP EndGetEvent(IMFAsyncResult *result, IMFMediaEvent **out) override { return events->EndGetEvent(result, out); }
    STDMETHODIMP QueueEvent(MediaEventType event, REFGUID extended, HRESULT status, const PROPVARIANT *value) override {
        return events->QueueEventParamVar(event, extended, status, value);
    }
    STDMETHODIMP GetMediaSink(IMFMediaSink **out) override;
    STDMETHODIMP GetIdentifier(DWORD *out) override { if (!out) return E_POINTER; *out = id; return S_OK; }
    STDMETHODIMP GetMediaTypeHandler(IMFMediaTypeHandler **out) override { if (!out) return E_POINTER; *out = this; AddRef(); return S_OK; }
    STDMETHODIMP IsMediaTypeSupported(IMFMediaType *candidate, IMFMediaType **closest) override {
        if (closest) *closest = nullptr;
        if (!candidate) return E_POINTER;
        GUID major{}, subtype{};
        HRESULT hr = candidate->GetGUID(MF_MT_MAJOR_TYPE, &major);
        if (SUCCEEDED(hr)) hr = candidate->GetGUID(MF_MT_SUBTYPE, &subtype);
        if (FAILED(hr)) return hr;
        return (id == 0 && major == MFMediaType_Video && subtype == MFVideoFormat_H264)
            || (id == 1 && major == MFMediaType_Audio && subtype == MFAudioFormat_AAC) ? S_OK : MF_E_INVALIDMEDIATYPE;
    }
    STDMETHODIMP GetMediaTypeCount(DWORD *out) override { if (!out) return E_POINTER; *out = 1; return S_OK; }
    STDMETHODIMP GetMediaTypeByIndex(DWORD index, IMFMediaType **out) override {
        if (index) return MF_E_NO_MORE_TYPES;
        return GetCurrentMediaType(out);
    }
    STDMETHODIMP SetCurrentMediaType(IMFMediaType *candidate) override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (stopped) return MF_E_SHUTDOWN;
        HRESULT hr = IsMediaTypeSupported(candidate, nullptr);
        if (FAILED(hr)) return hr;
        candidate->AddRef(); type->Release(); type = candidate;
        return S_OK;
    }
    STDMETHODIMP GetCurrentMediaType(IMFMediaType **out) override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (!out) return E_POINTER;
        *out = type; type->AddRef(); return S_OK;
    }
    STDMETHODIMP GetMajorType(GUID *out) override { return type->GetGUID(MF_MT_MAJOR_TYPE, out); }
    STDMETHODIMP ProcessSample(IMFSample *sample) override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (stopped) return MF_E_SHUTDOWN;
        if (!sample) return E_POINTER;
        // Repeat the small decoder configuration so a consumer can recover at an IDR
        // after a queue overflow without borrowing this sink's mutable media type.
        UINT size = 0;
        if (id == 0 && SUCCEEDED(type->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &size)) && size && size < 65536) {
            BYTE config[65536];
            HRESULT hr = type->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, config, size, nullptr);
            if (FAILED(hr)) return hr;
            callback(context, id, config, size, 0, 0, 0, 2);
        }
        if (id == 1) {
            UINT32 rate = 0, channels = 0;
            if (FAILED(type->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &rate))
                || FAILED(type->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels))) return MF_E_INVALIDMEDIATYPE;
            UINT frequencyIndex = rate == 48000 ? 3 : rate == 44100 ? 4 : 15;
            if (frequencyIndex == 15 || channels < 1 || channels > 2) return MF_E_INVALIDMEDIATYPE;
            BYTE config[2] = { static_cast<BYTE>((2 << 3) | (frequencyIndex >> 1)),
                static_cast<BYTE>((frequencyIndex << 7) | (channels << 3)) };
            callback(context, id, config, 2, 0, 0, 0, 2);
        }
        LONGLONG pts = 0, duration = 0;
        HRESULT hr = sample->GetSampleTime(&pts);
        if (FAILED(hr)) return hr;
        sample->GetSampleDuration(&duration);
        UINT64 decodeTime = static_cast<UINT64>(pts);
        sample->GetUINT64(MFSampleExtension_DecodeTimestamp, &decodeTime);
        UINT32 clean = 0;
        sample->GetUINT32(MFSampleExtension_CleanPoint, &clean);
        IMFMediaBuffer *buffer = nullptr;
        hr = sample->ConvertToContiguousBuffer(&buffer);
        if (FAILED(hr)) return hr;
        BYTE *data = nullptr; DWORD length = 0;
        hr = buffer->Lock(&data, nullptr, &length);
        if (SUCCEEDED(hr)) {
            callback(context, id, data, length, pts, static_cast<LONGLONG>(decodeTime), duration, clean ? 1 : 0);
            buffer->Unlock();
        }
        buffer->Release();
        return hr;
    }
    STDMETHODIMP PlaceMarker(MFSTREAMSINK_MARKER_TYPE, const PROPVARIANT *, const PROPVARIANT *value) override {
        return QueueEvent(MEStreamSinkMarker, GUID_NULL, S_OK, value);
    }
    STDMETHODIMP Flush() override { return S_OK; }
    void Shutdown() {
        std::lock_guard<std::recursive_mutex> lock(gate);
        stopped = true; owner = nullptr;
        if (events) events->Shutdown();
    }
};

class PacketSink final : public IMFMediaSink, public IMFClockStateSink {
    std::atomic<ULONG> refs{1};
    std::recursive_mutex gate;
    IMFPresentationClock *clock = nullptr;
    std::vector<PacketStream *> streams;
    PacketCallback callback;
    void *context;
    bool stopped = false;
public:
    PacketSink(PacketCallback cb, void *ctx) : callback(cb), context(ctx) {}
    ~PacketSink() { Shutdown(); }
    STDMETHODIMP QueryInterface(REFIID iid, void **out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IMFMediaSink)) *out = static_cast<IMFMediaSink *>(this);
        else if (iid == __uuidof(IMFClockStateSink)) *out = static_cast<IMFClockStateSink *>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG count = --refs; if (!count) delete this; return count; }
    STDMETHODIMP GetCharacteristics(DWORD *out) override { if (!out) return E_POINTER; *out = MEDIASINK_RATELESS; return S_OK; }
    STDMETHODIMP AddStreamSink(DWORD id, IMFMediaType *type, IMFStreamSink **out) override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (!out || !type) return E_POINTER;
        *out = nullptr;
        if (stopped) return MF_E_SHUTDOWN;
        if (id != streams.size() || id > 1) return MF_E_INVALIDSTREAMNUMBER;
        auto stream = new(std::nothrow) PacketStream(this, id, type, callback, context);
        if (!stream) return E_OUTOFMEMORY;
        HRESULT hr = stream->Initialise();
        if (FAILED(hr)) { stream->Release(); return hr; }
        try { streams.push_back(stream); }
        catch (const std::bad_alloc &) { stream->Release(); return E_OUTOFMEMORY; }
        *out = stream; stream->AddRef(); return S_OK;
    }
    STDMETHODIMP RemoveStreamSink(DWORD) override { return MF_E_STREAMSINKS_FIXED; }
    STDMETHODIMP GetStreamSinkCount(DWORD *out) override { if (!out) return E_POINTER; *out = static_cast<DWORD>(streams.size()); return S_OK; }
    STDMETHODIMP GetStreamSinkByIndex(DWORD index, IMFStreamSink **out) override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (!out) return E_POINTER;
        *out = nullptr;
        if (stopped) return MF_E_SHUTDOWN;
        if (index >= streams.size()) return MF_E_INVALIDINDEX;
        *out = streams[index]; (*out)->AddRef(); return S_OK;
    }
    STDMETHODIMP GetStreamSinkById(DWORD id, IMFStreamSink **out) override { return GetStreamSinkByIndex(id, out); }
    STDMETHODIMP SetPresentationClock(IMFPresentationClock *value) override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (stopped) return MF_E_SHUTDOWN;
        if (clock) { clock->RemoveClockStateSink(this); clock->Release(); }
        clock = value;
        if (clock) { clock->AddRef(); return clock->AddClockStateSink(this); }
        return S_OK;
    }
    STDMETHODIMP GetPresentationClock(IMFPresentationClock **out) override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (!out) return E_POINTER;
        *out = clock;
        if (!clock) return MF_E_NO_CLOCK;
        clock->AddRef(); return S_OK;
    }
    STDMETHODIMP Shutdown() override {
        std::lock_guard<std::recursive_mutex> lock(gate);
        if (stopped) return S_OK;
        stopped = true;
        if (clock) { clock->RemoveClockStateSink(this); clock->Release(); clock = nullptr; }
        for (auto stream : streams) { stream->Shutdown(); stream->Release(); }
        streams.clear(); return S_OK;
    }
    STDMETHODIMP OnClockStart(MFTIME, LONGLONG) override { return S_OK; }
    STDMETHODIMP OnClockStop(MFTIME) override { return S_OK; }
    STDMETHODIMP OnClockPause(MFTIME) override { return S_OK; }
    STDMETHODIMP OnClockRestart(MFTIME) override { return S_OK; }
    STDMETHODIMP OnClockSetRate(MFTIME, float) override { return S_OK; }
};

inline HRESULT PacketStream::GetMediaSink(IMFMediaSink **out) {
    std::lock_guard<std::recursive_mutex> lock(gate);
    if (!out) return E_POINTER;
    *out = owner;
    if (!owner) return MF_E_SHUTDOWN;
    owner->AddRef(); return S_OK;
}
