# GPU frame capture

## Capture contract

- `IGpuFrameCaptureRenderer` exposes draw-thread-only capture on Veldrid renderers.
- A bounded pool copies the swapchain colour target before presentation into device-local textures. Fence polling does not wait; pool exhaustion drops incoming frames without overwriting consumer leases.
- Each lease carries a sequence number and a monotonic submission timestamp. This timestamp does not measure display presentation or speaker output.
- Consumers must finish encoder/GPU access before returning leases on the draw thread. Stop capture and return leases before renderer shutdown.
- Resize recreates unused slots while retaining leased textures at their original dimensions. The live recorder currently stops on source-size changes.
- Vulkan capture requires transfer-source swapchain support and the sibling Veldrid changes. Image layout is restored after copying. A native image handle alone does not establish external-memory interoperability.

## Integration

`DesktopVideoRecording` connects live capture to native Windows H.264/AAC encoding, using either an MP4 sink or the RTMP/RTMPS packet publisher. Recording and streaming are mutually exclusive. Offline replay export owns a separate renderer and audio graph.

See [offline-video-export.md](offline-video-export.md), [vulkan-recording-streaming-design.md](vulkan-recording-streaming-design.md) and the sibling client's `docs/video-recording.md` for integration and limits.

WASAPI recording uses the native PCM tap; the playback callback does not enter a managed recording DSP. Format conversion and buffering run on a separate recording worker. Ordinary non-WASAPI audio uses BASS DSP taps. Missing audio is filled with silence. Audio/video timing and physical device restart behaviour remain unverified.

## Local build and release boundary

Build with `-p:G0V0SourceVeldrid=true` (framework) or `-p:G0V0SourceBuild=true` (client) to use the sibling Veldrid source. Published Veldrid/framework packages do not yet contain all APIs used here.

Use `native/build-recording.ps1` for build-only native compilation and explicit deployment with hash verification. The older native build scripts execute probes and are excluded from the current validation workflow.

## Validation boundary

Current integration has managed/native compilation and static checks only. Existing capture tests, native probes and historical results are preserved separately; they do not establish successful current gameplay recording, streaming or independent replay export. No tests, smoke checks, native probes or external streaming connections are run by this workflow.
