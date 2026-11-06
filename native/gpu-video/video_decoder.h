// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#pragma once
#include <objidl.h>
#include <mutex>

// Media Foundation video decoding for the framework's VideoDecoder on Windows. The managed stream is
// read through a positional callback, and frames are returned as I420 planes, the layout video textures expect.

// Exported by mfplat (Windows 7 and later) but missing from the MinGW headers.
extern "C" HRESULT WINAPI MFCreateMFByteStreamOnStream(IStream *stream, IMFByteStream **byteStream);

// Reads up to `size` bytes at `position`; returns the bytes read, 0 at the end, or -1 on failure.
using DecoderReadCallback = int (__cdecl *)(void *context, LONGLONG position, BYTE *buffer, int size);

// An IStream over the managed callback. Media Foundation wraps it with MFCreateMFByteStreamOnStream,
// which may call it from its own work queue threads; the position is kept here under a lock.
class CallbackStream final : public IStream {
    std::atomic<ULONG> refs{1};
    std::mutex gate;
    DecoderReadCallback read;
    void *context;
    LONGLONG length;
    LONGLONG position = 0;
public:
    CallbackStream(DecoderReadCallback callback, void *ctx, LONGLONG size) : read(callback), context(ctx), length(size) {}
    STDMETHODIMP QueryInterface(REFIID iid, void **out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(ISequentialStream) || iid == __uuidof(IStream)) *out = static_cast<IStream *>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG count = --refs; if (!count) delete this; return count; }
    STDMETHODIMP Read(void *buffer, ULONG size, ULONG *done) override {
        std::lock_guard<std::mutex> lock(gate);
        ULONG total = 0;
        while (total < size) {
            int chunk = (int)(size - total > 0x40000000 ? 0x40000000 : size - total);
            int count = read(context, position, static_cast<BYTE *>(buffer) + total, chunk);
            if (count < 0) { if (done) *done = total; return STG_E_READFAULT; }
            if (count == 0) break;
            total += (ULONG)count; position += count;
        }
        if (done) *done = total;
        return total < size ? S_FALSE : S_OK;
    }
    STDMETHODIMP Write(const void *, ULONG, ULONG *) override { return STG_E_ACCESSDENIED; }
    STDMETHODIMP Seek(LARGE_INTEGER move, DWORD origin, ULARGE_INTEGER *result) override {
        std::lock_guard<std::mutex> lock(gate);
        LONGLONG base = origin == STREAM_SEEK_SET ? 0 : origin == STREAM_SEEK_CUR ? position : origin == STREAM_SEEK_END ? length : -1;
        if (base < 0 || base + move.QuadPart < 0) return STG_E_INVALIDFUNCTION;
        position = base + move.QuadPart;
        if (result) result->QuadPart = (ULONGLONG)position;
        return S_OK;
    }
    STDMETHODIMP SetSize(ULARGE_INTEGER) override { return STG_E_ACCESSDENIED; }
    STDMETHODIMP CopyTo(IStream *, ULARGE_INTEGER, ULARGE_INTEGER *, ULARGE_INTEGER *) override { return E_NOTIMPL; }
    STDMETHODIMP Commit(DWORD) override { return S_OK; }
    STDMETHODIMP Revert() override { return E_NOTIMPL; }
    STDMETHODIMP LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return STG_E_INVALIDFUNCTION; }
    STDMETHODIMP UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return STG_E_INVALIDFUNCTION; }
    STDMETHODIMP Stat(STATSTG *stat, DWORD) override {
        if (!stat) return E_POINTER;
        ZeroMemory(stat, sizeof(*stat));
        stat->type = STGTY_STREAM;
        stat->cbSize.QuadPart = (ULONGLONG)length;
        stat->grfMode = STGM_READ;
        return S_OK;
    }
    STDMETHODIMP Clone(IStream **) override { return E_NOTIMPL; }
};

struct DecoderInfo {
    int width, height;          // Visible frame size.
    double frameRate;           // Frames per second, or 0 if unknown.
    LONGLONG duration;          // In 100 ns units, or 0 if unknown.
    int matrix;                 // MFVideoTransferMatrix: 1 for BT.709, 2 for BT.601, 0 if unknown.
    int canSeek;
};

// Calls may arrive on different managed threads, so each entry point balances its own COM initialisation.
// Media Foundation objects are free-threaded, and its work queue threads keep the multithreaded apartment alive.
struct ComScope {
    bool owns;
    HRESULT result;
    ComScope() { result = CoInitializeEx(nullptr, COINIT_MULTITHREADED); owns = SUCCEEDED(result); }
    ~ComScope() { if (owns) CoUninitialize(); }
    HRESULT Status() const { return FAILED(result) && result != RPC_E_CHANGED_MODE ? result : S_OK; }
};

struct VideoDecoding {
    IMFSourceReader *reader = nullptr;
    UINT32 frameWidth = 0, frameHeight = 0;   // Allocated size, which can exceed the visible size.
    LONG stride = 0;
    DecoderInfo info = {};
};

// The visible size, from an unshifted minimum display aperture.
static bool read_aperture(IMFMediaType *type, int *width, int *height) {
    MFVideoArea area = {};
    UINT32 size = 0;
    if (FAILED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, (UINT8 *)&area, sizeof(area), &size)) || size != sizeof(area)
        || area.Area.cx <= 0 || area.Area.cy <= 0 || area.OffsetX.value != 0 || area.OffsetY.value != 0)
        return false;
    *width = area.Area.cx; *height = area.Area.cy;
    return true;
}

static HRESULT update_decoder_format(VideoDecoding *d) {
    IMFMediaType *type = nullptr;
    HRESULT hr = d->reader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &type);
    if (FAILED(hr)) return hr;
    hr = MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &d->frameWidth, &d->frameHeight);
    UINT32 stride = 0;
    if (SUCCEEDED(hr) && FAILED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride)))
        hr = MFGetStrideForBitmapInfoHeader(MFVideoFormat_NV12.Data1, d->frameWidth, (LONG *)&stride);
    d->stride = (LONG)stride;
    if (SUCCEEDED(hr)) {
        if (!read_aperture(type, &d->info.width, &d->info.height)) {
            // Some decoders (HEVC) report the aperture only from their first frame; until then use the compressed stream's.
            d->info.width = (int)d->frameWidth; d->info.height = (int)d->frameHeight;
            IMFMediaType *native = nullptr;
            int width = 0, height = 0;
            if (SUCCEEDED(d->reader->GetNativeMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &native))
                && read_aperture(native, &width, &height) && width <= (int)d->frameWidth && height <= (int)d->frameHeight) {
                d->info.width = width; d->info.height = height;
            }
            release(native);
        }
        UINT32 numerator = 0, denominator = 0;
        if (SUCCEEDED(MFGetAttributeRatio(type, MF_MT_FRAME_RATE, &numerator, &denominator)) && denominator)
            d->info.frameRate = (double)numerator / denominator;
        d->info.matrix = (int)MFGetAttributeUINT32(type, MF_MT_YUV_MATRIX, 0);
        if (d->stride <= 0 || (UINT32)d->stride < d->frameWidth || d->info.width > (int)d->frameWidth || d->info.height > (int)d->frameHeight)
            hr = MF_E_INVALIDMEDIATYPE;
    }
    type->Release();
    return hr;
}

API HRESULT g0_vdec_open(DecoderReadCallback read, void *context, LONGLONG length, VideoDecoding **output, DecoderInfo *info) {
    if (!read || !output || !info || length < 0) return E_INVALIDARG;
    *output = nullptr;
    ComScope com;
    HRESULT hr = com.Status();
    if (FAILED(hr)) return hr;
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) return hr;
    auto d = new(std::nothrow) VideoDecoding();
    auto stream = new(std::nothrow) CallbackStream(read, context, length);
    IMFByteStream *bytes = nullptr;
    IMFAttributes *attributes = nullptr;
    IMFMediaType *type = nullptr;
    hr = d && stream ? S_OK : E_OUTOFMEMORY;
    CHECK(MFCreateMFByteStreamOnStream(stream, &bytes));
    CHECK(MFCreateAttributes(&attributes, 1));
    // Converts decoders' other YUV outputs to NV12 in software; most decoders produce NV12 already.
    CHECK(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE));
    if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromByteStream(bytes, attributes, &d->reader);
    CHECK(d->reader->SetStreamSelection((DWORD)MF_SOURCE_READER_ALL_STREAMS, FALSE));
    CHECK(d->reader->SetStreamSelection((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE));
    CHECK(MFCreateMediaType(&type));
    CHECK(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
    CHECK(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
    CHECK(d->reader->SetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, type));
    CHECK(update_decoder_format(d));
    if (SUCCEEDED(hr)) {
        PROPVARIANT value;
        PropVariantInit(&value);
        if (SUCCEEDED(d->reader->GetPresentationAttribute((DWORD)MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION, &value)) && value.vt == VT_UI8)
            d->info.duration = (LONGLONG)value.uhVal.QuadPart;
        PropVariantClear(&value);
        if (SUCCEEDED(d->reader->GetPresentationAttribute((DWORD)MF_SOURCE_READER_MEDIASOURCE, MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS, &value)) && value.vt == VT_UI4)
            d->info.canSeek = (value.ulVal & MFMEDIASOURCE_CAN_SEEK) && !(value.ulVal & MFMEDIASOURCE_HAS_SLOW_SEEK) ? 1 : 0;
        PropVariantClear(&value);
    }
    release(type); release(attributes); release(bytes);
    if (stream) stream->Release();
    if (FAILED(hr)) {
        if (d) { release(d->reader); delete d; }
        MFShutdown();
        return hr;
    }
    *info = d->info;
    *output = d;
    return S_OK;
}

// Decodes the next frame into I420 planes of the visible size: Y with stride `width`, then U and V with
// stride (width + 1) / 2. Returns S_FALSE at the end of the stream. `info` is refreshed if the format changes.
API HRESULT g0_vdec_read(VideoDecoding *d, BYTE *y, BYTE *u, BYTE *v, int capacityWidth, int capacityHeight, LONGLONG *time, DecoderInfo *info) {
    if (!d || !y || !u || !v || !time || !info) return E_INVALIDARG;
    ComScope com;
    if (FAILED(com.Status())) return com.Status();
    while (true) {
        DWORD flags = 0;
        LONGLONG timestamp = 0;
        IMFSample *sample = nullptr;
        HRESULT hr = d->reader->ReadSample((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, nullptr, &flags, &timestamp, &sample);
        if (FAILED(hr)) return hr;
        if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) {
            hr = update_decoder_format(d);
            *info = d->info;
            if (FAILED(hr)) { release(sample); return hr; }
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) { release(sample); return S_FALSE; }
        if (!sample) continue;
        const int width = d->info.width, height = d->info.height;
        // The caller sizes its planes from `info`; after a format change it must reallocate and read again.
        if (width > capacityWidth || height > capacityHeight) { sample->Release(); *info = d->info; return MF_E_INVALIDMEDIATYPE; }
        IMFMediaBuffer *buffer = nullptr;
        hr = sample->ConvertToContiguousBuffer(&buffer);
        sample->Release();
        if (FAILED(hr)) return hr;
        BYTE *data = nullptr;
        LONG pitch = d->stride;
        DWORD length = 0;
        IMF2DBuffer *planar = nullptr;
        bool locked2d = SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&planar))) && SUCCEEDED(planar->Lock2D(&data, &pitch));
        if (!locked2d) {
            release(planar);
            hr = buffer->Lock(&data, nullptr, &length);
            if (FAILED(hr)) { buffer->Release(); return hr; }
        }
        // NV12: the interleaved chroma plane follows the allocated luma rows.
        const LONGLONG required = (LONGLONG)pitch * d->frameHeight * 3 / 2;
        if (pitch <= 0 || (!locked2d && length < required)) hr = MF_E_BUFFERTOOSMALL;
        if (SUCCEEDED(hr)) {
            const int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
            for (int row = 0; row < height; row++)
                memcpy(y + (size_t)row * width, data + (size_t)row * pitch, (size_t)width);
            const BYTE *chroma = data + (size_t)pitch * d->frameHeight;
            for (int row = 0; row < chromaHeight; row++) {
                const BYTE *source = chroma + (size_t)row * pitch;
                BYTE *uRow = u + (size_t)row * chromaWidth, *vRow = v + (size_t)row * chromaWidth;
                for (int column = 0; column < chromaWidth; column++) {
                    uRow[column] = source[column * 2];
                    vRow[column] = source[column * 2 + 1];
                }
            }
            *time = timestamp;
        }
        if (locked2d) { planar->Unlock2D(); planar->Release(); }
        else buffer->Unlock();
        buffer->Release();
        return hr;
    }
}

// Seeks to `time` (100 ns units). Decoding resumes at the preceding key frame; the caller skips earlier frames.
API HRESULT g0_vdec_seek(VideoDecoding *d, LONGLONG time) {
    if (!d || time < 0) return E_INVALIDARG;
    ComScope com;
    if (FAILED(com.Status())) return com.Status();
    PROPVARIANT position;
    PropVariantInit(&position);
    position.vt = VT_I8;
    position.hVal.QuadPart = time;
    return d->reader->SetCurrentPosition(GUID_NULL, position);
}

API void g0_vdec_close(VideoDecoding *d) {
    if (!d) return;
    ComScope com;
    release(d->reader);
    delete d;
    MFShutdown();
}
