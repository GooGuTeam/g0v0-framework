// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <limits>
#define WUFFS_IMPLEMENTATION
#define WUFFS_CONFIG__STATIC_FUNCTIONS
#include "wuffs-unsupported-snapshot.c"
#define STB_IMAGE_WRITE_IMPLEMENTATION
#define STB_IMAGE_WRITE_STATIC
#include "stb_image_write.h"
#if defined(_WIN32)
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif
namespace {
constexpr size_t max_bytes = 256 * 1024 * 1024;
class Callbacks : public wuffs_aux::DecodeImageCallbacks {
 public:
  wuffs_base__pixel_format SelectPixfmt(const wuffs_base__image_config&) override {
    return wuffs_base__make_pixel_format(WUFFS_BASE__PIXEL_FORMAT__RGBA_NONPREMUL);
  }
  AllocPixbufResult AllocPixbuf(const wuffs_base__image_config& c, bool u) override {
    if (uint64_t(c.pixcfg.width()) * c.pixcfg.height() > max_bytes / 4)
      return AllocPixbufResult(std::string("Image exceeds pixel budget"));
    return DecodeImageCallbacks::AllocPixbuf(c, u);
  }
  AllocWorkbufResult AllocWorkbuf(wuffs_base__range_ii_u64 r, bool u) override {
    if (r.max_incl > max_bytes) return AllocWorkbufResult(std::string("Image exceeds work budget"));
    return DecodeImageCallbacks::AllocWorkbuf(r, u);
  }
};
struct Output { std::vector<uint8_t> bytes; bool failed = false; };
void write_chunk(void* context, void* data, int length) noexcept {
  auto& o = *static_cast<Output*>(context);
  if (o.failed) return;
  try {
    if (length < 0 || size_t(length) > max_bytes - o.bytes.size()) { o.failed = true; return; }
    auto p = static_cast<uint8_t*>(data);
    o.bytes.insert(o.bytes.end(), p, p + length);
  } catch (...) { o.failed = true; }
}
}
API uint32_t g0_image_abi() noexcept { return 1; }
API void g0_image_free(void* p) { free(p); }
API int g0_image_decode(const uint8_t* input, size_t length, uint8_t** pixels, int* width, int* height) noexcept {
  if (!pixels || !width || !height) return 0;
  *pixels = nullptr; *width = *height = 0;
  if (!input || !length || length > max_bytes) return 0;
  try {
    Callbacks callbacks;
    wuffs_aux::sync_io::MemoryInput source(input, length);
    auto result = wuffs_aux::DecodeImage(callbacks, source, wuffs_aux::DecodeImageArgQuirks::DefaultValue(),
      wuffs_aux::DecodeImageArgFlags::DefaultValue(), wuffs_aux::DecodeImageArgPixelBlend::DefaultValue(),
      wuffs_aux::DecodeImageArgBackgroundColor::DefaultValue(), wuffs_aux::DecodeImageArgMaxInclDimension(32768));
    if (!result.error_message.empty()) return 0;
    auto table = result.pixbuf.plane(0);
    auto w = result.pixbuf.pixcfg.width(), h = result.pixbuf.pixcfg.height();
    size_t row = size_t(w) * 4, count = row * h;
    if (!w || !h || count > max_bytes || table.width < row || table.height < h) return 0;
    auto dst = static_cast<uint8_t*>(malloc(count));
    if (!dst) return 0;
    for (uint32_t y = 0; y < h; ++y) memcpy(dst + y * row, table.ptr + y * table.stride, row);
    *pixels = dst; *width = int(w); *height = int(h);
    return 1;
  } catch (...) { return 0; }
}
// format: 0 = PNG, 1 = JPEG, 2 = BMP.
API int g0_image_encode(const uint8_t* pixels, size_t length, int width, int height,
                       int format, int quality, uint8_t** output, size_t* output_length) noexcept {
  if (!output || !output_length) return 0;
  *output = nullptr; *output_length = 0;
  if (!pixels || width <= 0 || height <= 0 || width > 32768 || height > 32768 ||
      uint64_t(width) * height * 4 > max_bytes || uint64_t(width) * height * 4 != length) return 0;
  try {
    Output sink;
    int ok = 0;
    if (format == 0) ok = stbi_write_png_to_func(write_chunk, &sink, width, height, 4, pixels, width * 4);
    else if (format == 1 && quality >= 1 && quality <= 100)
      ok = stbi_write_jpg_to_func(write_chunk, &sink, width, height, 4, pixels, quality);
    else if (format == 2) ok = stbi_write_bmp_to_func(write_chunk, &sink, width, height, 4, pixels);
    if (!ok || sink.failed || sink.bytes.empty()) return 0;
    auto dst = static_cast<uint8_t*>(malloc(sink.bytes.size()));
    if (!dst) return 0;
    memcpy(dst, sink.bytes.data(), sink.bytes.size());
    *output = dst; *output_length = sink.bytes.size();
    return 1;
  } catch (...) { return 0; }
}
