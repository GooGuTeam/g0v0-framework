# Native recording and streaming

`recorder.cpp` provides live/offline D3D11 recording. `vulkan_recording.cpp` adds same-adapter Vulkan/D3D11 sharing. `packet_sink.h` delivers encoded H.264/AAC packets to the managed RTMP/RTMPS publisher. See [offline-video-export.md](../../docs/offline-video-export.md) for framework ownership and client integration.

## Build and deploy

Use Zig 0.14.1 and Vulkan headers. Close the client before deployment.

```powershell
../build-recording.ps1 -Zig C:/tools/zig/zig.exe -VulkanHeaders C:/tools/Vulkan-Headers/include -DeployDirectory C:/path/to/client/output
```

The script compiles both libraries with `-Wall -Wextra -Werror` and runs no probes or tests. Outputs:

- `../artifacts/win-x64/g0v0-gpu-video.dll`
- `../artifacts/win-x64/g0v0-wasapi-capture.dll`

See [native integration](../README.md) for MSBuild, toolchain pinning and CI/CD packaging.

`-DeployDirectory` copies both DLLs to an existing directory and verifies SHA-256 hashes. Without it, client copies remain unchanged. Restart the client after deployment; configured recording requires all four `*_open_configured` entry points.

The client recorder supplies PCM from the native WASAPI tap when WASAPI is active, or BASS mixer taps on ordinary backends. Deploy the capture DLL beside the application or select it via an absolute `G0V0_WASAPI_CAPTURE_LIBRARY` path. WASAPI recording does not install a managed DSP on the playback graph. Offline export uses a private mixer. The old `build-win.ps1` executes probes and is unsuitable for build-only validation.

## Encoding contract

- The encoder requires a D3D11-aware hardware H.264 transform. Software-only Media Foundation selection is rejected.
- Frame rate, target bitrate, H.264 profile and output dimensions come from the configured open calls. Legacy open entry points retain their original defaults for standalone callers.
- `PooledConverter` caches video-processing resources and up to 16 NV12 surfaces. Scaling preserves aspect ratio with black borders.
- Input surfaces remain leased until `IMFTrackedSample` completion. A failed write may still return a completion ticket, which must be released.
- Live capture drops frames under backpressure and preserves capture-time gaps. Offline capture waits on its dedicated worker and requires contiguous frame indices.
- Audio accepts interleaved float PCM at 44.1/48 kHz, mono/stereo, with explicit sample positions.
- Sink modes are MP4 or encoded-packet callbacks. Simultaneous file/network fanout is not implemented.
- D3D11 immediate-context multithread protection is required. Conversion requests full-range RGB to studio-range BT.709; decoded colours and metadata need runtime validation.

## Validation

Current managed/native builds pass. End-to-end replay export and streaming, encoder/vendor coverage, AV drift, device loss and colour accuracy remain unverified.

Earlier standalone D3D11 work encoded and decoded alternating 640×360 frames with synthetic audio using an NVIDIA hardware MFT. Earlier Vulkan shared-texture checks are recorded in [VULKAN-INTEROP-PROBE.md](VULKAN-INTEROP-PROBE.md). These results apply to those standalone runs, not the current client integration. Diagnostic transform dumps must remain disabled in production streaming processes.
