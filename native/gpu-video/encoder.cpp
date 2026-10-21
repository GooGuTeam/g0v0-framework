// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include <windows.h>
#include <d3d11.h>
#include <d3d10.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <new>
#include <codecapi.h>
#include "packet_sink.h"
#ifdef G0_VIDEO_DIAGNOSTICS
#include <stdio.h>
#endif

#define API extern "C" __declspec(dllexport)
#include "frame_completion.h"

template<class T> static void release(T *&p) { if (p) { p->Release(); p = nullptr; } }
struct Encoder {
    IMFSinkWriter *writer = nullptr;
    IMFDXGIDeviceManager *manager = nullptr;
    PacketSink *packetSink = nullptr;
    ID3D11Device *device = nullptr;
    DWORD stream = 0, audioStream = 0;
    UINT audioRate = 0, audioChannels = 0;
    LONGLONG audioPosition = 0;
    UINT width = 0, height = 0, fps = 0;
    LONGLONG next = 0;
    bool started = false;
};

// All functions must execute on one COM-initialised encoding thread. The caller owns
// renderer-device synchronisation; submit only completed copies and retain textures
// until the encoder's COM references are released. No CPU mapping is performed here.
static HRESULT open_video(ID3D11Device *device, const wchar_t *path, UINT width, UINT height,
                          UINT fps, UINT bitrate, UINT audioRate, UINT audioChannels, Encoder **output,
                          PacketCallback callback = nullptr, void *callbackContext = nullptr, UINT profile = 0) {
    if (!device || (!path && !callback) || !output || !width || !height || (width & 1) || (height & 1) || !fps || !bitrate)
        return E_INVALIDARG;
    *output = nullptr;
    if ((profile != 0 && profile != 66 && profile != 77 && profile != 100) || fps > 120 || bitrate > 100000000)
        return E_INVALIDARG;
    if ((audioRate == 0) != (audioChannels == 0) || (audioRate && audioRate != 48000 && audioRate != 44100) || audioChannels > 2)
        return E_INVALIDARG;
    // MF may use the immediate context from internal threads. Refuse an unsafe
    // device rather than silently changing the game's global locking policy.
    ID3D10Multithread *threading = nullptr;
    HRESULT threadingResult = device->QueryInterface(__uuidof(ID3D10Multithread), (void **)&threading);
    if (FAILED(threadingResult)) return threadingResult;
    bool protectedContext = threading->GetMultithreadProtected() != FALSE;
    release(threading);
    if (!protectedContext) return MF_E_INVALIDREQUEST;
    Encoder *e = new(std::nothrow) Encoder();
    if (!e) return E_OUTOFMEMORY;
    IMFAttributes *attributes = nullptr;
    IMFMediaType *encoded = nullptr, *input = nullptr;
    UINT token = 0;
    HRESULT hr = MFCreateDXGIDeviceManager(&token, &e->manager);
#ifdef G0_VIDEO_DIAGNOSTICS
#define CHECK(call) if (SUCCEEDED(hr)) { hr = (call); if (FAILED(hr)) fprintf(stderr, "%s: 0x%08lx\n", #call, (unsigned long)hr); }
#else
#define CHECK(call) if (SUCCEEDED(hr)) hr = (call)
#endif
    CHECK(e->manager->ResetDevice(device, token));
    CHECK(MFCreateAttributes(&attributes, 4));
    CHECK(attributes->SetUnknown(MF_SINK_WRITER_D3D_MANAGER, e->manager));
    CHECK(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE));
    CHECK(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE));
    if (!callback) { CHECK(MFCreateSinkWriterFromURL(path, nullptr, attributes, &e->writer)); }
    CHECK(MFCreateMediaType(&encoded));
    CHECK(encoded->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    CHECK(encoded->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
    CHECK(encoded->SetUINT32(MF_MT_AVG_BITRATE, bitrate));
    if (profile) { CHECK(encoded->SetUINT32(MF_MT_MPEG2_PROFILE, profile)); }
    CHECK(encoded->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    CHECK(MFSetAttributeSize(encoded, MF_MT_FRAME_SIZE, width, height));
    CHECK(MFSetAttributeRatio(encoded, MF_MT_FRAME_RATE, fps, 1));
    CHECK(MFSetAttributeRatio(encoded, MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    if (callback && SUCCEEDED(hr)) {
        e->packetSink = new(std::nothrow) PacketSink(callback, callbackContext);
        if (!e->packetSink) hr = E_OUTOFMEMORY;
        IMFStreamSink *streamSink = nullptr;
        CHECK(e->packetSink->AddStreamSink(0, encoded, &streamSink));
        release(streamSink);
        if (audioRate) {
            IMFMediaType *aac = nullptr;
            CHECK(MFCreateMediaType(&aac));
            CHECK(aac->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
            CHECK(aac->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
            CHECK(aac->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, audioChannels));
            CHECK(aac->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, audioRate));
            CHECK(aac->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
            CHECK(aac->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000));
            CHECK(aac->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0));
            CHECK(e->packetSink->AddStreamSink(1, aac, &streamSink));
            release(streamSink); release(aac);
            e->audioStream = 1;
        }
        CHECK(attributes->SetUINT32(MF_LOW_LATENCY, TRUE));
        CHECK(MFCreateSinkWriterFromMediaSink(e->packetSink, attributes, &e->writer));
    } else { CHECK(e->writer->AddStream(encoded, &e->stream)); }
    CHECK(MFCreateMediaType(&input));
    CHECK(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    // Input is GPU-converted NV12. Hardware transforms are requested, NOT
    // guaranteed; probe/telemetry must verify selection.
    CHECK(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    CHECK(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
    CHECK(MFSetAttributeSize(input, MF_MT_FRAME_SIZE, width, height));
    CHECK(MFSetAttributeRatio(input, MF_MT_FRAME_RATE, fps, 1));
    CHECK(MFSetAttributeRatio(input, MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
    CHECK(e->writer->SetInputMediaType(e->stream, input, nullptr));
    if (SUCCEEDED(hr) && audioRate) {
        IMFMediaType *aac = nullptr, *pcm = nullptr;
        CHECK(MFCreateMediaType(&aac));
        CHECK(aac->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
        CHECK(aac->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC));
        CHECK(aac->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, audioChannels));
        CHECK(aac->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, audioRate));
        CHECK(aac->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16));
        CHECK(aac->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000));
        CHECK(aac->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0));
        if (!callback) { CHECK(e->writer->AddStream(aac, &e->audioStream)); }
        CHECK(MFCreateMediaType(&pcm));
        CHECK(pcm->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio));
        CHECK(pcm->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float));
        CHECK(pcm->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, audioChannels));
        CHECK(pcm->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, audioRate));
        CHECK(pcm->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 32));
        CHECK(pcm->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, audioChannels * 4));
        CHECK(pcm->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, audioRate * audioChannels * 4));
        CHECK(e->writer->SetInputMediaType(e->audioStream, pcm, nullptr));
        release(aac); release(pcm);
    }
    CHECK(e->writer->BeginWriting());
    // Do not silently pass a software MFT off as hardware encoding.
    if (SUCCEEDED(hr)) {
        IMFSinkWriterEx *extended = nullptr;
        bool hardware = false;
        if (SUCCEEDED(e->writer->QueryInterface(__uuidof(IMFSinkWriterEx), (void **)&extended))) {
            for (DWORD i = 0; i < 16; i++) {
                GUID category;
                IMFTransform *transform = nullptr;
                if (FAILED(extended->GetTransformForStream(e->stream, i, &category, &transform))) break;
                IMFAttributes *settings = nullptr;
                if (IsEqualGUID(category, MFT_CATEGORY_VIDEO_ENCODER) && SUCCEEDED(transform->GetAttributes(&settings))) {
                    UINT32 aware = 0, length = 0;
                    settings->GetUINT32(MF_SA_D3D11_AWARE, &aware);
                    hardware = aware && SUCCEEDED(settings->GetStringLength(MFT_ENUM_HARDWARE_URL_Attribute, &length)) && length > 0;
                    release(settings);
                }
                if (callback && IsEqualGUID(category, MFT_CATEGORY_VIDEO_ENCODER)) {
                    ICodecAPI *codec = nullptr;
                    if (SUCCEEDED(transform->QueryInterface(IID_ICodecAPI, (void **)&codec))) {
                        VARIANT setting = {}; setting.vt = VT_UI4;
                        setting.ulVal = 0; codec->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &setting);
                        setting.ulVal = fps * 2; codec->SetValue(&CODECAPI_AVEncMPVGOPSize, &setting);
                        codec->Release();
                    }
                }
                release(transform);
                if (hardware) break;
            }
            release(extended);
        }
        if (!hardware) hr = MF_E_TOPO_CODEC_NOT_FOUND;
    }
    release(input); release(encoded); release(attributes);
    if (FAILED(hr)) {
        release(e->writer); release(e->manager);
        if (e->packetSink) e->packetSink->Shutdown();
        release(e->packetSink); delete e; return hr;
    }
    e->device = device; device->AddRef();
    e->width = width; e->height = height; e->fps = fps; e->started = true;
    e->audioRate = audioRate; e->audioChannels = audioChannels;
    *output = e;
    return S_OK;
}

API HRESULT g0_video_open(ID3D11Device *device, const wchar_t *path, UINT width, UINT height,
    UINT fps, UINT bitrate, UINT audioRate, UINT audioChannels, Encoder **output) {
    return open_video(device, path, width, height, fps, bitrate, audioRate, audioChannels, output);
}

API HRESULT g0_video_keyframe(Encoder *e) {
    if (!e || !e->writer) return E_INVALIDARG;
    IMFSinkWriterEx *writer = nullptr;
    HRESULT hr = e->writer->QueryInterface(__uuidof(IMFSinkWriterEx), (void **)&writer);
    if (FAILED(hr)) return hr;
    hr = E_NOINTERFACE;
    for (DWORD i = 0; i < 16; i++) {
        GUID category; IMFTransform *transform = nullptr;
        if (FAILED(writer->GetTransformForStream(e->stream, i, &category, &transform))) break;
        if (category == MFT_CATEGORY_VIDEO_ENCODER) {
            ICodecAPI *codec = nullptr;
            hr = transform->QueryInterface(IID_ICodecAPI, (void **)&codec);
            if (SUCCEEDED(hr)) {
                VARIANT value = {}; value.vt = VT_UI4; value.ulVal = 1;
                hr = codec->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &value);
                codec->Release();
            }
            transform->Release(); break;
        }
        transform->Release();
    }
    writer->Release(); return hr;
}

API HRESULT g0_video_write(Encoder *e, ID3D11Texture2D *texture, FrameCompletion **completion) {
    if (!completion) return E_POINTER;
    *completion = nullptr;
    if (!e || !texture || !e->started) return E_INVALIDARG;
    D3D11_TEXTURE2D_DESC desc;
    texture->GetDesc(&desc);
    if (desc.Width != e->width || desc.Height != e->height || desc.Format != DXGI_FORMAT_NV12 || desc.SampleDesc.Count != 1)
        return E_INVALIDARG;
    ID3D11Device *owner = nullptr;
    texture->GetDevice(&owner);
    bool same = owner == e->device;
    release(owner);
    if (!same) return E_INVALIDARG;
    IMFMediaBuffer *buffer = nullptr;
    IMFSample *sample = nullptr;
    IMFTrackedSample *tracked = nullptr;
    auto ticket = new(std::nothrow) FrameCompletion();
    if (!ticket) return E_OUTOFMEMORY;
    HRESULT hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), texture, 0, FALSE, &buffer);
    CHECK(buffer->SetCurrentLength(e->width * e->height * 3 / 2));
    CHECK(MFCreateTrackedSample(&tracked));
    CHECK(tracked->QueryInterface(__uuidof(IMFSample), (void **)&sample));
    CHECK(sample->AddBuffer(buffer));
    LONGLONG time = e->next * 10000000 / e->fps;
    LONGLONG end = (e->next + 1) * 10000000 / e->fps;
    CHECK(sample->SetSampleTime(time));
    CHECK(sample->SetSampleDuration(end - time));
    // After SetAllocator succeeds, return a ticket even if WriteSample fails: the
    // caller must not guess whether a failing transform retained the sample.
    CHECK(tracked->SetAllocator(ticket, nullptr));
    bool armed = SUCCEEDED(hr);
    CHECK(e->writer->WriteSample(e->stream, sample));
    release(sample); release(tracked); release(buffer);
    if (armed) *completion = ticket;
    else ticket->Release();
    if (SUCCEEDED(hr)) e->next++;
    return hr;
}

// Worker-thread only: caller supplies interleaved float PCM, never from a realtime callback.
// Explicit sample-frame positions preserve gaps rather than concatenating late audio.
API HRESULT g0_video_audio(Encoder *e, const float *pcm, UINT frames, LONGLONG position) {
    if (!e || !e->started || !e->audioRate || !pcm || !frames || frames > e->audioRate || position < e->audioPosition
        || position > LLONG_MAX / 10000000 - frames) return E_INVALIDARG;
    IMFMediaBuffer *buffer = nullptr;
    IMFSample *sample = nullptr;
    DWORD bytes = frames * e->audioChannels * sizeof(float);
    HRESULT hr = MFCreateMemoryBuffer(bytes, &buffer);
    BYTE *destination = nullptr;
    CHECK(buffer->Lock(&destination, nullptr, nullptr));
    if (SUCCEEDED(hr)) {
        memcpy(destination, pcm, bytes);
        hr = buffer->Unlock();
    }
    CHECK(buffer->SetCurrentLength(bytes));
    CHECK(MFCreateSample(&sample));
    CHECK(sample->AddBuffer(buffer));
    CHECK(sample->SetSampleTime(position * 10000000 / e->audioRate));
    CHECK(sample->SetSampleDuration((position + frames) * 10000000 / e->audioRate - position * 10000000 / e->audioRate));
    if (position != e->audioPosition) { CHECK(sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE)); }
    CHECK(e->writer->WriteSample(e->audioStream, sample));
    release(sample); release(buffer);
    if (SUCCEEDED(hr)) e->audioPosition = position + frames;
    return hr;
}

API HRESULT g0_video_close(Encoder *e) {
    if (!e) return E_INVALIDARG;
    HRESULT hr = e->started ? e->writer->Finalize() : S_OK;
    release(e->writer); release(e->manager); release(e->device);
    if (e->packetSink) e->packetSink->Shutdown();
    release(e->packetSink); delete e;
    return hr;
}
