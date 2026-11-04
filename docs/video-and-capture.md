# Video, recording and capture

Framework support for live recording and streaming, offline replay export, frame capture and video decoding. The client's controls and settings are in its `docs/video-recording.md`; native build steps are in [native/gpu-video/README.md](../native/gpu-video/README.md).

## Frame capture

`IGpuFrameCaptureRenderer` (Veldrid renderers) copies the swapchain colour target before presentation into a bounded pool of device-local textures, on the draw thread.

- Fences are polled without waiting; when the pool is exhausted, new frames are dropped rather than overwriting leased ones.
- Each lease carries a sequence number and a submission timestamp (not a display or audio time). Consumers return leases on the draw thread, and must stop capture and return them before the renderer shuts down.
- Resizing recreates unused slots; leased textures keep their size. Live recording stops when the source size changes.
- Vulkan capture needs transfer-source swapchain support; the image layout is restored after the copy.
- Leases expose the Veldrid texture and device, so consumers can also read frames back themselves; the client's cross-platform FFmpeg recorder copies each lease into its own staging textures and maps them once their fences signal.

## Live recording and streaming (Windows)

`DesktopVideoRecording` encodes captured frames and the game's audio with Media Foundation H.264/AAC on a bounded worker queue, into an MP4 file or into packets for `RtmpVideoPublisher`. Recording and streaming are mutually exclusive.

- **D3D11:** frames are converted to NV12 and scaled on the GPU, preserving aspect ratio.
- **Vulkan:** a D3D11 encoding device is opened on the same adapter (matched by LUID), and a shared BGRA texture is imported into Vulkan. Veldrid submits the capture copies on the draw thread (`BackendInfoVulkan.SubmitExternalCapture`); native code never submits to the Vulkan queue. Requires `VulkanCaptureInterop` before startup and matching adapter support.
- **Audio:** `AudioManager.BeginCapture()` mixes 44.1 kHz stereo float. WASAPI output is tapped natively ([wasapi-native-callback.md](wasapi-native-callback.md)) and converted on a capture worker into a two-second ring; other backends use BASS DSP taps. Missing audio is filled with silence, and capture survives device restarts.
- **Streaming:** `RtmpVideoPublisher` turns H.264/AAC into FLV for RTMP, or RTMPS with certificate validation. Queues are bounded by packets, bytes and duration; congestion drops dependent frames up to the next keyframe and requests one. Reconnection resends codec headers. Publishing addresses stay in memory and are removed from error messages.

## Offline replay export

`OfflineRenderContext` owns a windowless device (D3D11 on Windows, Vulkan on Linux, Metal on macOS; `OfflineVulkanRenderer` forces Vulkan). `OfflineFrameRenderer` draws a private scene into a fixed-size framebuffer, and `OfflineAudioMixer` drives a private mixer on BASS's no-sound device.

- **Timeline:** 30, 60 or 120 FPS with 48 kHz stereo audio (1600, 800 or 400 samples per frame); gameplay advances in 50-sample steps and one frame is rendered per video frame. Time comes from the output sample position and the constant mod rate, never from the wall clock, so variable-rate mods are rejected.
- **Encoders:** native Media Foundation H.264 on Windows (frames reach the encoder one frame late, once their GPU conversion completes; `Complete()` flushes the last one), or FFmpeg with libx264/AAC on any desktop, fed raw frames through pipelined readback.
- **Ownership:** a dedicated worker owns the device, audio, scene and encoder, including teardown, which runs in reverse dependency order. Live textures, draw nodes and audio channels never enter the export context. Publishing the finished file (`.partial.mp4`, then a move) is the client's job.
- **Scene host:** `CreateSceneHost(storage)` returns an `OfflineSceneHost`, a `GameHost` that never runs, for the scene to resolve in place of the live host. It exposes the context's renderer, the given storage (also as cache storage), the default texture loader store and screen suspension, and has no window, input handlers or threads (`UpdateThread`, `DrawThread` are `null`), so nothing in the scene can pick up a wall-clock driven clock or touch the live window. Disposing it does nothing; it must not outlive the context.

1080p60 per-frame cost on an RTX 5060 Laptop: render only about 3.5 ms; native encoding 3.4–4.1 ms; FFmpeg readback 4.4–6.1 ms (excluding FFmpeg's own encoding).

## Video decoding

On Windows x64, `WindowsGameHost.CreateVideoDecoder` returns `MediaFoundationVideoDecoder` (`UseMediaFoundationVideoDecoder`, on), implemented in the native GPU video library:

- The managed stream is read through an `IStream` behind `MFCreateMFByteStreamOnStream`. The source reader outputs NV12, which native code copies into I420 planes for the video textures. Decoding is software.
- Timestamps start at the first frame, and seeks go to the preceding keyframe and skip to the target, as with FFmpeg. Colour conversion follows `MF_MT_YUV_MATRIX`.
- Streams Media Foundation cannot open (FLV, or HEVC/VP9 without the Windows video extensions) fall back to FFmpeg when it is present.

The first frames of H.264 (MP4, MOV, MKV, AVI), HEVC, VP8 and VP9 test videos decode identically to FFmpeg. Windows x64 builds leave out FFmpeg's native libraries (`native/WindowsFfmpegExclusion.targets`, carried in the package's `buildTransitive`), saving about 7 MB; without them, files Media Foundation cannot open do not play. MPEG-4 Part 2 AVI files (common in old beatmaps) and hardware decoding are untested.

## Not verified

Live streaming against a real endpoint and across encoder vendors; device loss, resize and timestamp wrap during recording; long-session audio/video drift; FFmpeg export on Linux and macOS; Vulkan capture on adapters other than NVIDIA and Intel.
