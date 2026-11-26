// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#pragma once
#include <objidl.h>
#include <mutex>
#include <vector>
#include <d3d11_1.h>
#include <d3dcompiler.h>

// Media Foundation video decoding for the framework's VideoDecoder on Windows. The managed stream is
// read through a positional callback, and frames are returned as I420 planes, the layout video textures expect.
// With hardware decoding, the decoder and video processor run on a private D3D11 device (DXVA) and each frame is
// read back from its texture; Media Foundation falls back to its software decoders when the GPU lacks the codec.
// With shared output (experimental), frames never reach the CPU: a compute shader splits each NV12 frame into Y, U and V
// planes in a shared texture (see SharedFrame), which the renderer's device copies from (video_share.h).

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
    int hardware;               // Whether the stream decodes on the GPU (DXVA); updated from the first frame.
    int shared;                 // Whether frames are delivered as shared textures (g0_vdec_read_shared).
};

// A decoded frame in a shared R8 texture: Y at (0, 0), U at (0, height) and V at ((width + 1) / 2, height), each chroma
// plane (width + 1) / 2 by (height + 1) / 2. The consumer acquires its keyed mutex with key 1 and releases it with key 0,
// then reports the slot with g0_vdec_release_shared.
struct SharedFrame {
    HANDLE handle;              // NT handle of the texture, owned by the decoder; the same for every frame of a slot and generation.
    int slot, generation;
    int textureWidth, textureHeight;
};

static const int share_slot_count = 4;

struct ShareSlot {
    ID3D11Texture2D *texture = nullptr;
    IDXGIKeyedMutex *mutex = nullptr;
    HANDLE handle = nullptr;
    bool busy = false;          // Handed out and not yet released by the consumer.
    void free() { if (handle) CloseHandle(handle); handle = nullptr; release(mutex); release(texture); busy = false; }
};

struct VideoShare {
    ID3D11DeviceContext *context = nullptr;
    ID3D10Multithread *threading = nullptr;
    ID3D11ComputeShader *shader = nullptr;
    ID3D11Buffer *constants = nullptr;
    ID3D11Texture2D *source = nullptr;          // The decoded NV12 frame, copied so the shader can read it.
    ID3D11ShaderResourceView *luma = nullptr, *chroma = nullptr;
    ID3D11Texture2D *planes = nullptr;          // Y, U and V written by the shader, then copied into a slot.
    ID3D11UnorderedAccessView *planesView = nullptr;
    UINT sourceWidth = 0, sourceHeight = 0;
    int width = 0, height = 0, generation = 0, next = 0;
    ShareSlot slots[share_slot_count];
    // Slots of earlier sizes, kept until the decoder closes because frames already handed out may not have been opened yet.
    std::vector<ShareSlot> retired;
    std::mutex gate;                            // Slot state, shared by the decoding thread and releases from the renderer.
    void freeTextures() {
        release(luma); release(chroma); release(source); release(planesView); release(planes);
        sourceWidth = sourceHeight = 0;
        width = height = 0;
    }
    ~VideoShare() {
        freeTextures();
        for (auto &slot : slots) slot.free();
        for (auto &slot : retired) slot.free();
        release(constants); release(shader); release(threading); release(context);
    }
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
    ID3D11Device *device = nullptr;
    IMFDXGIDeviceManager *manager = nullptr;
    VideoShare *share = nullptr;
    IMFSample *pending = nullptr;              // A decoded frame waiting for a free slot.
    LONGLONG pendingTime = 0;
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

// The adapter with the given LUID, or null if there is none.
static IDXGIAdapter1 *find_adapter(const LUID &luid) {
    IDXGIFactory1 *factory = nullptr;
    IDXGIAdapter1 *adapter = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void **)&factory))) return nullptr;
    for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
        DXGI_ADAPTER_DESC1 desc;
        if (SUCCEEDED(adapter->GetDesc1(&desc)) && desc.AdapterLuid.LowPart == luid.LowPart && desc.AdapterLuid.HighPart == luid.HighPart) break;
        release(adapter);
    }
    factory->Release();
    return adapter;
}

// A device for DXVA decoding, on the adapter with `adapterLuid` when given. Media Foundation uses its immediate context
// from its own threads, so it is multithread protected.
static HRESULT create_decoding_device(VideoDecoding *d, const LUID *adapterLuid) {
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
    IDXGIAdapter1 *adapter = adapterLuid ? find_adapter(*adapterLuid) : nullptr;
    if (adapterLuid && !adapter) return DXGI_ERROR_NOT_FOUND;
    HRESULT hr = D3D11CreateDevice(adapter, adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_VIDEO_SUPPORT, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &d->device, nullptr, nullptr);
    release(adapter);
    ID3D10Multithread *threading = nullptr;
    CHECK(d->device->QueryInterface(__uuidof(ID3D10Multithread), (void **)&threading));
    if (SUCCEEDED(hr)) threading->SetMultithreadProtected(TRUE);
    release(threading);
    UINT token = 0;
    CHECK(MFCreateDXGIDeviceManager(&token, &d->manager));
    CHECK(d->manager->ResetDevice(d->device, token));
    if (FAILED(hr)) { release(d->manager); release(d->device); }
    return hr;
}

// Splits an NV12 frame into the Y, U and V planes of a shared frame (see SharedFrame).
static const char share_shader_source[] = R"(
Texture2D<float> luma : register(t0);
Texture2D<float2> chroma : register(t1);
RWTexture2D<unorm float> planes : register(u0);
cbuffer Size : register(b0) { uint width; uint height; uint chromaWidth; uint chromaHeight; };
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    if (id.x < width && id.y < height) planes[id.xy] = luma[id.xy];
    if (id.x < chromaWidth && id.y < chromaHeight) {
        float2 c = chroma[id.xy];
        planes[uint2(id.x, height + id.y)] = c.x;
        planes[uint2(chromaWidth + id.x, height + id.y)] = c.y;
    }
}
)";

static HRESULT create_share(VideoDecoding *d) {
    // The compiler ships with Windows 10 and later; it is loaded only when shared output is requested.
    static HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
    if (!compiler) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
    FARPROC address = GetProcAddress(compiler, "D3DCompile");
    pD3DCompile compile;
    memcpy(&compile, &address, sizeof(compile));
    if (!compile) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    auto s = new(std::nothrow) VideoShare();
    if (!s) return E_OUTOFMEMORY;
    ID3DBlob *code = nullptr, *errors = nullptr;
    HRESULT hr = compile(share_shader_source, sizeof(share_shader_source) - 1, "g0v0-video-share", nullptr, nullptr, "main", "cs_5_0",
                         D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
    if (errors) {
        if (FAILED(hr)) fprintf(stderr, "%s\n", (const char *)errors->GetBufferPointer());
        errors->Release();
    }
    CHECK(d->device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &s->shader));
    release(code);
    D3D11_BUFFER_DESC constants = {};
    constants.ByteWidth = 16;
    constants.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    CHECK(d->device->CreateBuffer(&constants, nullptr, &s->constants));
    CHECK(d->device->QueryInterface(__uuidof(ID3D10Multithread), (void **)&s->threading));
    if (SUCCEEDED(hr)) d->device->GetImmediateContext(&s->context);
    if (FAILED(hr)) { delete s; return hr; }
    d->share = s;
    return S_OK;
}

// `hardware` requests DXVA decoding; without a usable D3D11 device the stream is still decoded in software.
// `adapter`, when given, is the LUID of the renderer's adapter, where the decoding device is created. `share` also requests
// shared output, which needs that device; `info->shared` reports whether it is used.
API HRESULT g0_vdec_open(DecoderReadCallback read, void *context, LONGLONG length, int hardware, const LUID *adapter, int share,
                         VideoDecoding **output, DecoderInfo *info) {
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
    CHECK(MFCreateAttributes(&attributes, 3));
    // Converts decoders' other YUV outputs to NV12 (on the GPU with a device); most decoders produce NV12 already.
    CHECK(attributes->SetUINT32(MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE));
    if (SUCCEEDED(hr) && (hardware || share) && SUCCEEDED(create_decoding_device(d, adapter))) {
        CHECK(attributes->SetUnknown(MF_SOURCE_READER_D3D_MANAGER, d->manager));
        CHECK(attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE));
        if (SUCCEEDED(hr) && share && FAILED(create_share(d))) fprintf(stderr, "Shared video output is unavailable\n");
    }
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
        if (d) { release(d->reader); delete d->share; release(d->manager); release(d->device); delete d; }
        MFShutdown();
        return hr;
    }
    d->info.shared = d->share ? 1 : 0;
    *info = d->info;
    *output = d;
    return S_OK;
}

// Decodes the next frame into I420 planes of the visible size: Y with stride `width`, then U and V with
// stride (width + 1) / 2. Returns S_FALSE at the end of the stream. `info` is refreshed with each frame and on format changes.
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
        // Hardware frames are texture (array) slices that DXVA may allocate taller than the frame; the chroma plane follows those rows.
        UINT32 allocatedHeight = d->frameHeight;
        IMFDXGIBuffer *surface = nullptr;
        d->info.hardware = 0;
        if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&surface)))) {
            d->info.hardware = 1;
            ID3D11Texture2D *texture = nullptr;
            if (SUCCEEDED(surface->GetResource(IID_PPV_ARGS(&texture)))) {
                D3D11_TEXTURE2D_DESC desc;
                texture->GetDesc(&desc);
                if (desc.Height >= d->frameHeight) allocatedHeight = desc.Height;
                texture->Release();
            }
            surface->Release();
        }
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
        const LONGLONG required = (LONGLONG)pitch * allocatedHeight * 3 / 2;
        if (pitch <= 0 || (!locked2d && length < required)) hr = MF_E_BUFFERTOOSMALL;
        if (SUCCEEDED(hr)) {
            const int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
            for (int row = 0; row < height; row++)
                memcpy(y + (size_t)row * width, data + (size_t)row * pitch, (size_t)width);
            const BYTE *chroma = data + (size_t)pitch * allocatedHeight;
            for (int row = 0; row < chromaHeight; row++) {
                const BYTE *source = chroma + (size_t)row * pitch;
                BYTE *uRow = u + (size_t)row * chromaWidth, *vRow = v + (size_t)row * chromaWidth;
                for (int column = 0; column < chromaWidth; column++) {
                    uRow[column] = source[column * 2];
                    vRow[column] = source[column * 2 + 1];
                }
            }
            *time = timestamp;
            *info = d->info;
        }
        if (locked2d) { planar->Unlock2D(); planar->Release(); }
        else buffer->Unlock();
        buffer->Release();
        return hr;
    }
}

// Recreates the frame-sized textures, and starts a new generation of slots, when the frame size changes.
static HRESULT ensure_share_textures(VideoDecoding *d) {
    VideoShare *s = d->share;
    const int width = d->info.width, height = d->info.height;
    if (s->source && s->sourceWidth == d->frameWidth && s->sourceHeight == d->frameHeight && s->width == width && s->height == height)
        return S_OK;
    s->freeTextures();
    HRESULT hr = S_OK;
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = d->frameWidth;
    desc.Height = d->frameHeight;
    desc.MipLevels = desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_NV12;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    CHECK(d->device->CreateTexture2D(&desc, nullptr, &s->source));
    D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
    view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    view.Texture2D.MipLevels = 1;
    view.Format = DXGI_FORMAT_R8_UNORM;
    CHECK(d->device->CreateShaderResourceView(s->source, &view, &s->luma));
    view.Format = DXGI_FORMAT_R8G8_UNORM;
    CHECK(d->device->CreateShaderResourceView(s->source, &view, &s->chroma));
    const int chromaWidth = (width + 1) / 2, chromaHeight = (height + 1) / 2;
    desc.Width = (UINT)(chromaWidth * 2);
    desc.Height = (UINT)(height + chromaHeight);
    desc.Format = DXGI_FORMAT_R8_UNORM;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    CHECK(d->device->CreateTexture2D(&desc, nullptr, &s->planes));
    CHECK(d->device->CreateUnorderedAccessView(s->planes, nullptr, &s->planesView));
    if (SUCCEEDED(hr)) {
        UINT size[4] = { (UINT)width, (UINT)height, (UINT)chromaWidth, (UINT)chromaHeight };
        s->context->UpdateSubresource(s->constants, 0, nullptr, size, 0, 0);
    }
    {
        std::lock_guard<std::mutex> lock(s->gate);
        for (auto &slot : s->slots) {
            if (slot.texture || slot.handle) s->retired.push_back(slot);
            slot = ShareSlot();
        }
        s->generation++;
        s->next = 0;
    }
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    for (auto &slot : s->slots) {
        IDXGIResource1 *resource = nullptr;
        CHECK(d->device->CreateTexture2D(&desc, nullptr, &slot.texture));
        CHECK(slot.texture->QueryInterface(__uuidof(IDXGIKeyedMutex), (void **)&slot.mutex));
        CHECK(slot.texture->QueryInterface(__uuidof(IDXGIResource1), (void **)&resource));
        CHECK(resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &slot.handle));
        release(resource);
    }
    if (FAILED(hr)) {
        s->freeTextures();
        return hr;
    }
    s->sourceWidth = d->frameWidth;
    s->sourceHeight = d->frameHeight;
    s->width = width;
    s->height = height;
    return S_OK;
}

// The next decoded sample, or a frame kept back for lack of a free slot. Returns S_FALSE at the end of the stream.
static HRESULT next_sample(VideoDecoding *d, IMFSample **sample, LONGLONG *time, DecoderInfo *info) {
    if (d->pending) {
        *sample = d->pending;
        *time = d->pendingTime;
        d->pending = nullptr;
        return S_OK;
    }
    while (true) {
        DWORD flags = 0;
        IMFSample *next = nullptr;
        HRESULT hr = d->reader->ReadSample((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, nullptr, &flags, time, &next);
        if (FAILED(hr)) return hr;
        if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) {
            hr = update_decoder_format(d);
            *info = d->info;
            if (FAILED(hr)) { release(next); return hr; }
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) { release(next); return S_FALSE; }
        if (next) { *sample = next; return S_OK; }
    }
}

// Copies a decoded sample into the NV12 source texture: on the GPU for DXVA frames, uploaded for software-decoded ones.
static HRESULT upload_sample(VideoDecoding *d, IMFSample *sample) {
    VideoShare *s = d->share;
    IMFMediaBuffer *buffer = nullptr;
    HRESULT hr = sample->ConvertToContiguousBuffer(&buffer);
    if (FAILED(hr)) return hr;
    IMFDXGIBuffer *surface = nullptr;
    if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&surface)))) {
        ID3D11Texture2D *texture = nullptr;
        UINT subresource = 0;
        CHECK(surface->GetResource(IID_PPV_ARGS(&texture)));
        CHECK(surface->GetSubresourceIndex(&subresource));
        if (SUCCEEDED(hr)) {
            D3D11_BOX box = { 0, 0, 0, d->frameWidth, d->frameHeight, 1 };
            s->context->CopySubresourceRegion(s->source, 0, 0, 0, 0, texture, subresource, &box);
        }
        release(texture);
        surface->Release();
        d->info.hardware = 1;
    } else {
        BYTE *data = nullptr;
        LONG pitch = d->stride;
        DWORD length = 0;
        IMF2DBuffer *planar = nullptr;
        bool locked2d = SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&planar))) && SUCCEEDED(planar->Lock2D(&data, &pitch));
        if (!locked2d) {
            release(planar);
            hr = buffer->Lock(&data, nullptr, &length);
            if (SUCCEEDED(hr) && length < (DWORD)((LONGLONG)pitch * d->frameHeight * 3 / 2)) hr = MF_E_BUFFERTOOSMALL;
        }
        if (SUCCEEDED(hr) && pitch <= 0) hr = MF_E_INVALIDMEDIATYPE;
        // NV12 uploads take the chroma plane right after the texture's luma rows, as in a software decoder's buffer.
        if (SUCCEEDED(hr)) s->context->UpdateSubresource(s->source, 0, nullptr, data, (UINT)pitch, 0);
        if (locked2d) { planar->Unlock2D(); planar->Release(); }
        else if (data) buffer->Unlock();
        d->info.hardware = 0;
    }
    buffer->Release();
    return hr;
}

static const HRESULT share_busy = (HRESULT)2;

// Decodes the next frame into a shared texture slot (see SharedFrame). Returns S_FALSE at the end of the stream, and 2 when
// every slot is still with the consumer; the frame is then kept for the next call.
API HRESULT g0_vdec_read_shared(VideoDecoding *d, SharedFrame *frame, LONGLONG *time, DecoderInfo *info) {
    if (!d || !frame || !time || !info) return E_INVALIDARG;
    if (!d->share) return HRESULT_FROM_WIN32(ERROR_INVALID_STATE);
    ComScope com;
    if (FAILED(com.Status())) return com.Status();
    VideoShare *s = d->share;
    IMFSample *sample = nullptr;
    HRESULT hr = next_sample(d, &sample, time, info);
    if (hr != S_OK) return hr;
    // Media Foundation also uses the immediate context; keep this sequence of state changes together.
    s->threading->Enter();
    hr = ensure_share_textures(d);
    int index = -1;
    if (SUCCEEDED(hr)) {
        std::lock_guard<std::mutex> lock(s->gate);
        // A free slot is not handed out, and its consumer has released it (key 0).
        for (int i = 0; i < share_slot_count && index < 0; i++) {
            int candidate = (s->next + i) % share_slot_count;
            if (!s->slots[candidate].busy && s->slots[candidate].mutex->AcquireSync(0, 0) == S_OK) index = candidate;
        }
        if (index >= 0) {
            s->slots[index].busy = true;
            s->next = index + 1;
        }
    }
    if (SUCCEEDED(hr) && index < 0) {
        s->threading->Leave();
        d->pending = sample;
        d->pendingTime = *time;
        return share_busy;
    }
    CHECK(upload_sample(d, sample));
    sample->Release();
    if (SUCCEEDED(hr)) {
        ID3D11ShaderResourceView *views[2] = { s->luma, s->chroma };
        s->context->CSSetShader(s->shader, nullptr, 0);
        s->context->CSSetShaderResources(0, 2, views);
        s->context->CSSetUnorderedAccessViews(0, 1, &s->planesView, nullptr);
        s->context->CSSetConstantBuffers(0, 1, &s->constants);
        s->context->Dispatch((UINT)(s->width + 15) / 16, (UINT)(s->height + 15) / 16, 1);
        ID3D11ShaderResourceView *noViews[2] = {};
        ID3D11UnorderedAccessView *noTarget = nullptr;
        s->context->CSSetShaderResources(0, 2, noViews);
        s->context->CSSetUnorderedAccessViews(0, 1, &noTarget, nullptr);
        s->context->CSSetShader(nullptr, nullptr, 0);
        s->context->CopyResource(s->slots[index].texture, s->planes);
    }
    if (index >= 0) {
        ShareSlot &slot = s->slots[index];
        // Hand the slot to the consumer (key 1), or keep it free (key 0) if the frame could not be produced.
        HRESULT released = slot.mutex->ReleaseSync(SUCCEEDED(hr) ? 1 : 0);
        if (SUCCEEDED(hr)) hr = released;
        if (SUCCEEDED(hr)) {
            D3D11_TEXTURE2D_DESC desc;
            slot.texture->GetDesc(&desc);
            frame->handle = slot.handle;
            frame->slot = index;
            frame->generation = s->generation;
            frame->textureWidth = (int)desc.Width;
            frame->textureHeight = (int)desc.Height;
        } else {
            std::lock_guard<std::mutex> lock(s->gate);
            slot.busy = false;
        }
    }
    s->threading->Leave();
    *info = d->info;
    return hr;
}

// Returns a slot handed out by g0_vdec_read_shared. `consumed` tells whether the consumer acquired and released it;
// otherwise the decoder takes it back from key 1 itself. Slots of an earlier generation are ignored.
API HRESULT g0_vdec_release_shared(VideoDecoding *d, int generation, int slot, int consumed) {
    if (!d || !d->share || slot < 0 || slot >= share_slot_count) return E_INVALIDARG;
    VideoShare *s = d->share;
    std::lock_guard<std::mutex> lock(s->gate);
    ShareSlot &target = s->slots[slot];
    if (generation != s->generation || !target.busy) return S_OK;
    HRESULT hr = S_OK;
    if (!consumed) {
        hr = target.mutex->AcquireSync(1, 1000);
        if (hr == S_OK) hr = target.mutex->ReleaseSync(0);
        else hr = FAILED(hr) ? hr : HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }
    target.busy = false;
    return hr;
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
    release(d->pending);
    return d->reader->SetCurrentPosition(GUID_NULL, position);
}

API void g0_vdec_close(VideoDecoding *d) {
    if (!d) return;
    ComScope com;
    release(d->pending);
    release(d->reader);
    delete d->share;
    release(d->manager);
    release(d->device);
    delete d;
    MFShutdown();
}
