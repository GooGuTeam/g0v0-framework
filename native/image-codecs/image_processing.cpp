// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>
#include <algorithm>
#include "Simd/SimdResizer.h"
#include "Simd/SimdEnable.h"
#include "Simd/SimdBase.h"
#include "Simd/SimdSse41.h"
#include "Simd/SimdAvx2.h"
#include "Simd/SimdNeon.h"
#if defined(_WIN32)
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif
namespace {
bool valid(int w, int h, size_t bytes) {
    return w > 0 && h > 0 && w <= 32768 && h <= 32768 &&
        uint64_t(w) * h * 4 <= 256 * 1024 * 1024 && uint64_t(w) * h * 4 == bytes;
}
void* create_resizer(size_t sw, size_t sh, size_t dw, size_t dh, SimdResizeMethodType method) {
#ifdef SIMD_AVX2_ENABLE
    if (Simd::Avx2::Enable) return Simd::Avx2::ResizerInit(sw, sh, dw, dh, 4, SimdResizeChannelByte, method);
#endif
#ifdef SIMD_SSE41_ENABLE
    if (Simd::Sse41::Enable) return Simd::Sse41::ResizerInit(sw, sh, dw, dh, 4, SimdResizeChannelByte, method);
#endif
#ifdef SIMD_NEON_ENABLE
    if (Simd::Neon::Enable) return Simd::Neon::ResizerInit(sw, sh, dw, dh, 4, SimdResizeChannelByte, method);
#endif
    return Simd::Base::ResizerInit(sw, sh, dw, dh, 4, SimdResizeChannelByte, method);
}
}
// Packed RGBA, straight alpha. Buffers are distinct and owned by the caller.
API int g0_image_resize(const uint8_t* src, size_t src_bytes, int sw, int sh,
                        uint8_t* dst, size_t dst_bytes, int dw, int dh, int method) noexcept {
    if (!src || !dst || src == dst || !valid(sw, sh, src_bytes) || !valid(dw, dh, dst_bytes) || method < 0 || method > 1)
        return 0;
    try {
        if (sw == dw && sh == dh) { memcpy(dst, src, src_bytes); return 1; }
        // Simd cubic kernels require at least two source samples per axis.
        int padded_w = std::max(sw, 2), padded_h = std::max(sh, 2);
        const size_t padded_bytes = size_t(padded_w) * padded_h * 4;
        // Keep source/destination distinct: SIMD tail handling may revisit overlapping lanes.
        if (padded_bytes * 2 + dst_bytes > 512 * 1024 * 1024) return 0;
        std::vector<uint8_t> input(padded_bytes), premultiplied(padded_bytes), resized(dst_bytes);
        for (int y = 0; y < padded_h; ++y)
            for (int x = 0; x < padded_w; ++x)
                memcpy(input.data() + (size_t(y) * padded_w + x) * 4,
                       src + (size_t(std::min(y, sh - 1)) * sw + std::min(x, sw - 1)) * 4, 4);
        auto premultiply = Simd::Base::AlphaPremultiply;
        auto unpremultiply = Simd::Base::AlphaUnpremultiply;
#ifdef SIMD_AVX2_ENABLE
        if (Simd::Avx2::Enable && padded_w >= 8) premultiply = Simd::Avx2::AlphaPremultiply;
        if (Simd::Avx2::Enable && dw >= 8) unpremultiply = Simd::Avx2::AlphaUnpremultiply;
#endif
        premultiply(input.data(), size_t(padded_w) * 4, padded_w, padded_h, premultiplied.data(), size_t(padded_w) * 4, SimdFalse);
        std::unique_ptr<Simd::Resizer> resizer(static_cast<Simd::Resizer*>(create_resizer(
            padded_w, padded_h, dw, dh, method == 0 ? SimdResizeMethodBilinear : SimdResizeMethodBicubic)));
        if (!resizer) return 0;
        resizer->Run(premultiplied.data(), size_t(padded_w) * 4, resized.data(), size_t(dw) * 4);
        // Cubic negative lobes may produce RGB larger than alpha.
        if (method == 1)
            for (size_t i = 0; i < dst_bytes; i += 4)
                for (size_t c = 0; c < 3; ++c) resized[i + c] = std::min(resized[i + c], resized[i + 3]);
        unpremultiply(resized.data(), size_t(dw) * 4, dw, dh, dst, size_t(dw) * 4, SimdFalse);
        return 1;
    } catch (...) { return 0; }
}
