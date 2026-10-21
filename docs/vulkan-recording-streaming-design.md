# Vulkan recording and streaming

## Capture and encoding

The Windows path uses a D3D11 encoding device on the Vulkan renderer's physical adapter. `vulkan_recording.cpp` matches the Vulkan device LUID to a DXGI adapter and checks BGRA external-memory support before importing a shared D3D11 texture.

Veldrid owns graphics-queue submission. The encoding worker prepares commands and keyed-mutex metadata, then schedules submission through `BackendInfoVulkan.SubmitExternalCapture` on the draw thread. Native code does not submit directly to the Vulkan queue. Capture requires swapchain transfer-source support and the local Veldrid interop extensions.

The worker converts shared RGB frames to NV12 using `PooledConverter`, scaling to the configured output size while preserving aspect ratio. Encoder surfaces remain leased until tracked-sample completion. Bounded capture/encoding pools drop live frames under load. Offline export waits on its own worker and requires every frame to be accepted.

The native encoder uses Media Foundation H.264/AAC with either an MP4 sink or `PacketSink`. File recording and streaming are mutually exclusive; simultaneous fanout is not implemented.

## Packet transport

`DesktopVideoRecording` copies encoded callbacks into managed packets. `RtmpVideoPublisher` normalises AVC/AAC configuration and payloads for FLV, then sends them through `RtmpConnection`.

- Queues are bounded. Congestion discards dependent video packets and requests a keyframe.
- Reconnection re-establishes codec headers and stream timing.
- RTMPS validates TLS certificates.
- Publishing URLs remain in memory and are excluded from failure messages and process command lines.
- Network operations run outside draw/audio callbacks.

FFmpeg is a separate offline replay encoder. It receives raw video and private PCM; it is not used for live transport. See [offline-video-export.md](offline-video-export.md).

## Audio and timing

Live WASAPI audio comes from its native callback queue; ordinary backends use BASS mixer DSP taps. WASAPI decode mixers have no managed recording DSP. Device-format conversion runs on a dedicated capture worker, independently of encoder stalls, feeding a bounded two-second PCM ring. `MixerAudioCapture` mixes fixed 44.1 kHz stereo blocks on a continuous recording timeline, allowing 100 ms for producer arrival and filling gaps with silence. Device restart rebuilds producers without closing the encoder or RTMP connection. Video timestamps preserve capture-time gaps at the configured frame rate. Long-run drift, restart continuity, callback/GC latency and reconnect timing require runtime validation.

## Validation and limits

Managed and native builds passed. Current streaming integration has not been connected to an endpoint or runtime-validated across encoder vendors. Device loss, resize/teardown, network congestion, timestamp wrap and sustained AV synchronisation remain acceptance work.

Earlier standalone interop results are recorded in [VULKAN-INTEROP-PROBE.md](../native/gpu-video/VULKAN-INTEROP-PROBE.md). They cover shared-texture/keyed-mutex reuse on local NVIDIA and Intel adapters, without Khronos validation layers, and do not establish end-to-end recording or streaming correctness.

Live Vulkan capture is Windows-only. Linux/macOS FFmpeg support currently applies to independent replay export. Native interop, encoder selection and shared-handle ownership require separate implementations for other live-capture platforms.
