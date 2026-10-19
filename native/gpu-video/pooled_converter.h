// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#pragma once

// Worker-thread-owned converter. Input scratch is reused only after the worker's
// conversion fence completes; output slots are reused only after tracked samples release.
class PooledConverter {
    ID3D11VideoDevice *video = nullptr;
    ID3D11VideoContext *context = nullptr;
    ID3D11VideoProcessorEnumerator *enumerator = nullptr;
    ID3D11VideoProcessor *processor = nullptr;
    ID3D11Texture2D *scratch = nullptr;
    ID3D11VideoProcessorInputView *input = nullptr;
    ID3D11Texture2D *outputs[16] = {};
    ID3D11VideoProcessorOutputView *views[16] = {};
    UINT width = 0, height = 0, outputWidth = 0, outputHeight = 0;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
public:
    ~PooledConverter() {
        for (int i = 0; i < 16; i++) { release(views[i]); release(outputs[i]); }
        release(input); release(scratch); release(processor); release(enumerator); release(context); release(video);
    }
    HRESULT Initialise(ID3D11Device *device, ID3D11DeviceContext *immediate, const D3D11_TEXTURE2D_DESC &source, UINT targetWidth = 0, UINT targetHeight = 0) {
        if (source.SampleDesc.Count != 1 || source.ArraySize != 1) return E_INVALIDARG;
        format = source.Format;
        DXGI_FORMAT typed;
        switch (format) {
            case DXGI_FORMAT_B8G8R8A8_TYPELESS:
            case DXGI_FORMAT_B8G8R8A8_UNORM: typed = DXGI_FORMAT_B8G8R8A8_UNORM; break;
            case DXGI_FORMAT_R8G8B8A8_TYPELESS:
            case DXGI_FORMAT_R8G8B8A8_UNORM: typed = DXGI_FORMAT_R8G8B8A8_UNORM; break;
            default: return E_INVALIDARG;
        }
        width = source.Width; height = source.Height;
        outputWidth = targetWidth ? targetWidth : width;
        outputHeight = targetHeight ? targetHeight : height;
        HRESULT hr = device->QueryInterface(__uuidof(ID3D11VideoDevice), (void **)&video);
        CHECK(immediate->QueryInterface(__uuidof(ID3D11VideoContext), (void **)&context));
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content = {};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputWidth = width; content.OutputWidth = outputWidth;
        content.InputHeight = height; content.OutputHeight = outputHeight;
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        CHECK(video->CreateVideoProcessorEnumerator(&content, &enumerator));
        CHECK(video->CreateVideoProcessor(enumerator, 0, &processor));
        auto desc = source;
        desc.Format = typed;
        desc.MipLevels = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = desc.MiscFlags = 0;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        CHECK(device->CreateTexture2D(&desc, nullptr, &scratch));
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputDesc = {};
        inputDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        CHECK(video->CreateVideoProcessorInputView(scratch, enumerator, &inputDesc, &input));
        if (SUCCEEDED(hr)) {
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb = {}, yuv = {};
            yuv.YCbCr_Matrix = 1;
            yuv.Nominal_Range = 1;
            context->VideoProcessorSetStreamColorSpace(processor, 0, &rgb);
            context->VideoProcessorSetOutputColorSpace(processor, &yuv);
            context->VideoProcessorSetStreamAutoProcessingMode(processor, 0, FALSE);
            RECT sourceRect = {0, 0, (LONG)width, (LONG)height};
            RECT targetRect = {0, 0, (LONG)outputWidth, (LONG)outputHeight};
            // Preserve aspect ratio and centre the source inside a black output canvas.
            UINT scaledWidth = outputWidth, scaledHeight = (UINT)((UINT64)height * outputWidth / width);
            if (scaledHeight > outputHeight) {
                scaledHeight = outputHeight; scaledWidth = (UINT)((UINT64)width * outputHeight / height);
            }
            LONG left = (LONG)((outputWidth - scaledWidth) / 2), top = (LONG)((outputHeight - scaledHeight) / 2);
            RECT destinationRect = {left, top, left + (LONG)scaledWidth, top + (LONG)scaledHeight};
            D3D11_VIDEO_COLOR black = {}; black.RGBA.A = 1.0f;
            context->VideoProcessorSetOutputBackgroundColor(processor, FALSE, &black);
            context->VideoProcessorSetStreamSourceRect(processor, 0, TRUE, &sourceRect);
            context->VideoProcessorSetStreamDestRect(processor, 0, TRUE, &destinationRect);
            context->VideoProcessorSetOutputTargetRect(processor, TRUE, &targetRect);
        }
        return hr;
    }
    HRESULT Convert(ID3D11Device *device, ID3D11DeviceContext *immediate, ID3D11Texture2D *source, int slot, ID3D11Texture2D **output) {
        *output = nullptr;
        if (slot < 0 || slot >= 16) return E_INVALIDARG;
        D3D11_TEXTURE2D_DESC desc; source->GetDesc(&desc);
        if (desc.Width != width || desc.Height != height || desc.Format != format || desc.SampleDesc.Count != 1) return E_INVALIDARG;
        HRESULT hr = S_OK;
        if (!outputs[slot]) {
            desc.Width = outputWidth; desc.Height = outputHeight;
            desc.Format = DXGI_FORMAT_NV12;
            desc.MipLevels = 1;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET;
            desc.MiscFlags = desc.CPUAccessFlags = 0;
            desc.Usage = D3D11_USAGE_DEFAULT;
            CHECK(device->CreateTexture2D(&desc, nullptr, &outputs[slot]));
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC viewDesc = {};
            viewDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            CHECK(video->CreateVideoProcessorOutputView(outputs[slot], enumerator, &viewDesc, &views[slot]));
            if (FAILED(hr)) { release(views[slot]); release(outputs[slot]); return hr; }
        }
        immediate->CopySubresourceRegion(scratch, 0, 0, 0, 0, source, 0, nullptr);
        D3D11_VIDEO_PROCESSOR_STREAM stream = {};
        stream.Enable = TRUE;
        stream.pInputSurface = input;
        hr = context->VideoProcessorBlt(processor, views[slot], 0, 1, &stream);
        if (SUCCEEDED(hr)) { outputs[slot]->AddRef(); *output = outputs[slot]; }
        return hr;
    }
};
