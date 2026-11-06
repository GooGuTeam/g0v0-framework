# Media Foundation video decoding

On Windows x64, `WindowsGameHost.CreateVideoDecoder` returns `MediaFoundationVideoDecoder`, which decodes through Media Foundation in the native GPU video library (`native/gpu-video/video_decoder.h`, built into `g0v0-gpu-video.dll`). The aim is to need no FFmpeg on Windows for the formats Media Foundation supports, which would shrink the Windows package.

## Design

- `VideoDecoder`'s public members are virtual. The Media Foundation decoder overrides them and delegates to the FFmpeg implementation when Media Foundation cannot open a stream (for example FLV), or when the native library is missing. `UseMediaFoundationVideoDecoder = false` always uses FFmpeg.
- The managed stream is read through a positional callback behind an `IStream`, wrapped by `MFCreateMFByteStreamOnStream`. Media Foundation may call it from its work queue threads; reads are serialised by a lock.
- The source reader outputs NV12 (advanced video processing converts other YUV outputs). Native code copies the visible area into tightly packed I420 planes, the layout video textures expect; `VideoTextureUpload` now accepts such planes as well as FFmpeg frames.
- Decoding is software. Each native entry point balances its own COM initialisation, since open, decode and close can run on different threads.
- Frame times start at the first frame's timestamp, matching FFmpeg's subtraction of the stream start time. Seeks go to the preceding key frame and skip output until the target, as the FFmpeg decoder does.
- The visible size comes from the minimum display aperture. Some decoders (HEVC) report it only with their first frame, through a format change; the compressed stream's aperture is used until then.
- Colour conversion uses `MF_MT_YUV_MATRIX` (BT.709/BT.601), falling back to the FFmpeg decoder's size rule when unspecified.

## Results

`MediaFoundationVideoDecoderGpuTest` (explicit) decodes the first frame of each test video with both decoders on an offline D3D11 device and reads the Y, U and V planes back from the GPU:

| Video | Decoder used | Difference from FFmpeg |
| --- | --- | --- |
| h264.mp4, h264-hd.mp4, h264.mov, h264.mkv, h264.avi | Media Foundation | none (identical planes) |
| hevc.mp4 | Media Foundation | none |
| vp8.webm, vp9.webm | Media Foundation | none |
| h264.flv | FFmpeg fallback | none |

First frame times match within one frame (AVI: Media Foundation 0 ms, FFmpeg 16.7 ms). Seeking to 32 525 ms gave a frame at 32 533 ms, and decoding reached the end of the stream.

HEVC and VP9 rely on the Windows HEVC and VP9 Video Extensions, which this machine has; without them those files fall back to FFmpeg. MPEG-4 Part 2 (Xvid/DivX) AVI files, common in older beatmaps, were not tested. Playback in the game window, the OpenGL renderer's upload path and hardware decoding were not exercised.

## Package size

Windows x64 builds omit FFmpeg's native libraries (`avcodec-58`, `avformat-58`, `avutil-56`, `swscale-5`): `native/WindowsFfmpegExclusion.targets` removes them after package assets are resolved, for both win-x64 publishes and RID-less builds. The framework package carries it under `buildTransitive`, and the client's local source build imports it for every project except `osu.Framework.Tests`, whose comparison test needs FFmpeg. Set `G0V0KeepWindowsFfmpeg=true` to keep them. Other runtimes (win-arm64, Linux, macOS) are unchanged, and `FFmpeg.AutoGen.dll` stays because `VideoDecoder` references its types.

A `win-x64` publish of `osu.Desktop` lost exactly those four files, 6 937 600 bytes (425 962 821 → 419 025 221 bytes uncompressed). The native library grew by about 5 KB.

Without FFmpeg, FLV videos and codecs without a Windows extension (HEVC or VP9 without the Video Extensions, and anything else Media Foundation cannot open) do not play: the decoder logs that FFmpeg is unavailable and enters the faulted state. With the FFmpeg libraries removed from the test output, Media Foundation still decoded MP4 and seeked to the end, and FLV faulted within 0.3 s without crashing.
