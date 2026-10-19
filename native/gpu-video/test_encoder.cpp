// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include <d3d10.h>
#include "encoder.cpp"
#include "converter.cpp"
#include <stdio.h>

int wmain(int argc, wchar_t **argv) {
    if (argc != 2) { puts("Usage: test_encoder.exe output.mp4"); return 2; }
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 3;
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) { CoUninitialize(); return 4; }
    ID3D11Device *device = nullptr;
    ID3D11DeviceContext *context = nullptr;
    ID3D11Texture2D *texture = nullptr, *nv12 = nullptr, *dark = nullptr;
    ID3D11RenderTargetView *view = nullptr;
    ID3D11Query *query = nullptr;
    Encoder *encoder = nullptr;
    hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
        &device, nullptr, &context);
    // Media Foundation submits work from its own threads on this device.
    ID3D10Multithread *multithread = nullptr;
    CHECK(context->QueryInterface(__uuidof(ID3D10Multithread), (void **)&multithread));
    if (multithread) { multithread->SetMultithreadProtected(TRUE); release(multithread); }
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = 640; desc.Height = 360; desc.MipLevels = desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    CHECK(device->CreateTexture2D(&desc, nullptr, &texture));
    CHECK(device->CreateRenderTargetView(texture, nullptr, &view));
    D3D11_QUERY_DESC queryDesc = { D3D11_QUERY_EVENT, 0 };
    CHECK(device->CreateQuery(&queryDesc, &query));
    if (SUCCEEDED(hr)) {
        const float colour[4] = { 0.1f, 0.6f, 0.2f, 1.0f };
        context->ClearRenderTargetView(view, colour);
        context->End(query);
        context->Flush();
        ULONGLONG deadline = GetTickCount64() + 10000;
        while ((hr = context->GetData(query, nullptr, 0, 0)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        if (hr == S_FALSE) hr = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }
    CHECK(convert_nv12(device, context, texture, &nv12));
    if (SUCCEEDED(hr)) {
        context->End(query);
        context->Flush();
        ULONGLONG deadline = GetTickCount64() + 10000;
        while ((hr = context->GetData(query, nullptr, 0, 0)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        if (hr == S_FALSE) hr = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }
    if (SUCCEEDED(hr)) {
        const float black[4] = {0, 0, 0, 1};
        context->ClearRenderTargetView(view, black);
    }
    CHECK(convert_nv12(device, context, texture, &dark));
    CHECK(g0_video_open(device, argv[1], 640, 360, 30, 2000000, 48000, 2, &encoder));
    if (SUCCEEDED(hr)) {
        IMFSinkWriterEx *extended = nullptr;
        if (SUCCEEDED(encoder->writer->QueryInterface(__uuidof(IMFSinkWriterEx), (void **)&extended))) {
            for (DWORD index = 0; index < 16; index++) {
                GUID category;
                IMFTransform *transform = nullptr;
                if (FAILED(extended->GetTransformForStream(encoder->stream, index, &category, &transform))) break;
                IMFAttributes *attributes = nullptr;
                if (SUCCEEDED(transform->GetAttributes(&attributes))) {
                    UINT32 aware = 0;
                    attributes->GetUINT32(MF_SA_D3D11_AWARE, &aware);
                    printf("Transform %lu D3D11-aware: %u (not proof of hardware encoding)\n", (unsigned long)index, aware);
                    UINT32 attributeCount = 0;
                    attributes->GetCount(&attributeCount);
                    for (UINT32 a = 0; a < attributeCount; a++) {
                        GUID key; PROPVARIANT value;
                        PropVariantInit(&value);
                        if (SUCCEEDED(attributes->GetItemByIndex(a, &key, &value))) {
                            wchar_t keyText[40];
                            StringFromGUID2(key, keyText, 40);
                            wprintf(L"Attribute %ls type=%u", keyText, value.vt);
                            if (value.vt == VT_LPWSTR) wprintf(L" value=%ls", value.pwszVal);
                            if (value.vt == VT_UI4) wprintf(L" value=%lu", value.ulVal);
                            if (value.vt == VT_CLSID) {
                                wchar_t clsid[40]; StringFromGUID2(*value.puuid, clsid, 40); wprintf(L" value=%ls", clsid);
                            }
                            wprintf(L"\n");
                        }
                        PropVariantClear(&value);
                    }
                    release(attributes);
                }
                IPersist *persist = nullptr;
                if (SUCCEEDED(transform->QueryInterface(IID_IPersist, (void **)&persist))) {
                    CLSID selected;
                    if (SUCCEEDED(persist->GetClassID(&selected))) {
                        IMFActivate **activations = nullptr;
                        UINT32 count = 0;
                        MFT_REGISTER_TYPE_INFO outputType = { MFMediaType_Video, MFVideoFormat_H264 };
                        if (SUCCEEDED(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                                               nullptr, &outputType, &activations, &count))) {
                            bool matched = false;
                            for (UINT32 i = 0; i < count; i++) {
                                GUID candidate;
                                if (SUCCEEDED(activations[i]->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &candidate)) && IsEqualGUID(candidate, selected)) matched = true;
                                activations[i]->Release();
                            }
                            CoTaskMemFree(activations);
                            printf("Selected transform matches enumerated hardware H264 encoder: %s\n", matched ? "YES" : "NO");
                        }
                    }
                    persist->Release();
                }
                release(transform);
            }
            release(extended);
        }
    }
    // Reuse a bounded pool only when the tracked sample reports release. Alternate
    // bright and black frames to make premature overwrite observable in decoding.
    constexpr int poolSize = 16;
    ID3D11Texture2D *pool[poolSize] = {};
    FrameCompletion *tickets[poolSize] = {};
    if (nv12) nv12->GetDesc(&desc);
    for (int i = 0; i < poolSize && SUCCEEDED(hr); i++)
        hr = device->CreateTexture2D(&desc, nullptr, &pool[i]);
    for (int i = 0; i < 60 && SUCCEEDED(hr); i++) {
        int slot = i % poolSize;
        ULONGLONG deadline = GetTickCount64() + 10000;
        while (tickets[slot] && !g0_video_frame_complete(tickets[slot]) && GetTickCount64() < deadline) Sleep(1);
        if (tickets[slot] && !g0_video_frame_complete(tickets[slot])) { hr = HRESULT_FROM_WIN32(WAIT_TIMEOUT); break; }
        g0_video_frame_release(tickets[slot]); tickets[slot] = nullptr;
        context->CopyResource(pool[slot], (i % 2) ? dark : nv12);
        context->End(query); context->Flush();
        deadline = GetTickCount64() + 10000;
        while ((hr = context->GetData(query, nullptr, 0, 0)) == S_FALSE && GetTickCount64() < deadline) Sleep(1);
        if (hr == S_FALSE) hr = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        CHECK(g0_video_write(encoder, pool[slot], &tickets[slot]));
        float pcm[1600 * 2];
        for (int n = 0; n < 1600; n++) {
            float value = ((i * 1600 + n) % 100 < 50) ? 0.01f : -0.01f;
            pcm[n * 2] = pcm[n * 2 + 1] = value;
        }
        CHECK(g0_video_audio(encoder, pcm, 1600, i * 1600));
    }
    if (encoder) {
        HRESULT finalResult = g0_video_close(encoder);
        if (SUCCEEDED(hr)) hr = finalResult;
    }
    ULONGLONG completionDeadline = GetTickCount64() + 10000;
    for (auto ticket : tickets) {
        if (!ticket) continue;
        while (!g0_video_frame_complete(ticket) && GetTickCount64() < completionDeadline) Sleep(1);
        if (!g0_video_frame_complete(ticket)) hr = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        g0_video_frame_release(ticket);
    }
    printf("D3D11 surface -> MP4 result: 0x%08lx\n", (unsigned long)hr);
    for (auto &surface : pool) release(surface);
    release(dark); release(nv12); release(query); release(view); release(texture); release(context); release(device);
    MFShutdown(); CoUninitialize();
    return FAILED(hr) ? 1 : 0;
}
