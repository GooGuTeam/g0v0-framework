// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#include "recorder.cpp"
#include <stdio.h>

int wmain(int argc, wchar_t **argv) {
    if (argc != 2) return 2;
    ID3D11Device *device = nullptr;
    ID3D11DeviceContext *context = nullptr;
    ID3D11Texture2D *texture = nullptr;
    ID3D11RenderTargetView *view = nullptr;
    Recording *recording = nullptr;
    HRESULT hr = D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
        nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);
    CHECK(g0_record_prepare(device));
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = 640; desc.Height = 360; desc.MipLevels = desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    CHECK(device->CreateTexture2D(&desc,nullptr,&texture));
    CHECK(device->CreateRenderTargetView(texture,nullptr,&view));
    CHECK(g0_record_open(device,argv[1],640,360,48000,2,&recording));
    ULONGLONG start = GetTickCount64();
    for (int i = 0; i < 600 && SUCCEEDED(hr); i++) {
        const float colour[4] = {i % 2 ? 0.0f : 0.2f, i % 2 ? 0.0f : 0.6f, 0.0f, 1.0f};
        context->ClearRenderTargetView(view,colour);
        hr = g0_record_offline_frame(recording,texture,i);
        if (hr == S_FALSE) { hr = E_UNEXPECTED; break; }
        float pcm[800 * 2];
        for (int n = 0; n < 800; n++) pcm[n*2] = pcm[n*2+1] = ((i*800+n)%100 < 50) ? 0.01f : -0.01f;
        CHECK(g0_record_audio(recording,pcm,800,i*800));
    }
    if (recording) {
        printf("accepted=%llu poolDrops=%llu cadenceDrops=%llu encode_ms=%llu\n",
            recording->accepted,recording->poolDrops,recording->cadenceDrops,GetTickCount64()-start);
        if (recording->accepted != 600 || recording->poolDrops || recording->cadenceDrops) hr = E_FAIL;
        HRESULT closed = g0_record_close(recording);
        if (SUCCEEDED(hr)) hr = closed;
    }
    release(view); release(texture); release(context); release(device);
    printf("offline result=0x%08lx\n",(unsigned long)hr);
    return FAILED(hr) ? 1 : 0;
}
