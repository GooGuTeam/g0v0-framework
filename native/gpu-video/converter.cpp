// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Included after encoder.cpp; the converter uses the same D3D11 device/context.
static HRESULT convert_nv12(ID3D11Device *device, ID3D11DeviceContext *context,
                            ID3D11Texture2D *source, ID3D11Texture2D **output) {
    *output = nullptr;
    ID3D11VideoDevice *video = nullptr;
    ID3D11VideoContext *videoContext = nullptr;
    ID3D11VideoProcessorEnumerator *enumerator = nullptr;
    ID3D11VideoProcessor *processor = nullptr;
    ID3D11VideoProcessorInputView *input = nullptr;
    ID3D11VideoProcessorOutputView *target = nullptr;
    ID3D11Texture2D *texture = nullptr, *typedSource = nullptr;
    D3D11_TEXTURE2D_DESC desc;
    source->GetDesc(&desc);
#ifdef G0_VIDEO_DIAGNOSTICS
    fprintf(stderr, "Capture surface: %ux%u format=%u bind=%u usage=%u samples=%u\n", desc.Width, desc.Height, desc.Format, desc.BindFlags, desc.Usage, desc.SampleDesc.Count);
#endif
    HRESULT hr = device->QueryInterface(__uuidof(ID3D11VideoDevice), (void **)&video);
    CHECK(context->QueryInterface(__uuidof(ID3D11VideoContext), (void **)&videoContext));
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC content = {};
    content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    content.InputWidth = content.OutputWidth = desc.Width;
    content.InputHeight = content.OutputHeight = desc.Height;
    content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    CHECK(video->CreateVideoProcessorEnumerator(&content, &enumerator));
    CHECK(video->CreateVideoProcessor(enumerator, 0, &processor));
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputDesc = {};
    inputDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    // Veldrid uses typeless resources for view reinterpretation. Video input
    // views require a concrete format; copy on GPU into a compatible UNORM resource.
    if (SUCCEEDED(hr) && (desc.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS || desc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS)) {
        auto typedDesc = desc;
        typedDesc.Format = desc.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
        typedDesc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        hr = device->CreateTexture2D(&typedDesc, nullptr, &typedSource);
        if (SUCCEEDED(hr)) context->CopyResource(typedSource, source);
    }
    CHECK(video->CreateVideoProcessorInputView(typedSource ? typedSource : source, enumerator, &inputDesc, &input));
    desc.Format = DXGI_FORMAT_NV12;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    desc.MiscFlags = 0;
    CHECK(device->CreateTexture2D(&desc, nullptr, &texture));
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputDesc = {};
    outputDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    CHECK(video->CreateVideoProcessorOutputView(texture, enumerator, &outputDesc, &target));
    if (SUCCEEDED(hr)) {
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb = {}, yuv = {};
        rgb.RGB_Range = 0; // Full-range RGB.
        yuv.YCbCr_Matrix = 1; // BT.709.
        yuv.Nominal_Range = 1; // Studio range.
        videoContext->VideoProcessorSetStreamColorSpace(processor, 0, &rgb);
        videoContext->VideoProcessorSetOutputColorSpace(processor, &yuv);
        videoContext->VideoProcessorSetStreamAutoProcessingMode(processor, 0, FALSE);
        D3D11_VIDEO_PROCESSOR_STREAM stream = {};
        stream.Enable = TRUE;
        stream.pInputSurface = input;
        hr = videoContext->VideoProcessorBlt(processor, target, 0, 1, &stream);
    }
    release(target); release(input); release(typedSource); release(processor); release(enumerator); release(videoContext); release(video);
    if (FAILED(hr)) release(texture);
    else *output = texture;
    return hr;
}
