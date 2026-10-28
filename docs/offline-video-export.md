# Offline replay video export

## Implementation

`OfflineRenderContext` owns a windowless D3D11 (Windows), Vulkan (Linux) or Metal (macOS) device. `OfflineFrameRenderer` draws a private scene into a fixed-size framebuffer. `OfflineAudioMixer.CreateAudioManager()` supplies private sample/track stores feeding a pull-driven mixer on the BASS no-sound device. `IOfflineVideoRecording` separates frame production from encoding. `OfflineVideoRecording` passes textures and PCM to the native Media Foundation encoder. `FfmpegOfflineVideoRecording` reads RGBA through a staging texture with row-pitch/orientation handling, pipes video into FFmpeg/libx264, spools PCM and performs a final AAC/MP4 mux. FFmpeg is an externally installed executable, invoked without a shell; cancellation terminates owned processes and cleanup removes temporary files.

The client integration is in `osu.Game/Screens/Play/OfflineReplayExportSession.cs` in the sibling client repository. The results-screen video action loads a real replay, creates a dedicated worker and private gameplay tree, and reports progress/cancellation without taking over the live window. The scene uses the production ruleset, replay input, score/health processors, skins, HUD, background and storyboard.

### Timeline and ownership

- Output defaults to 1920×1080 at 60 FPS, with 48 kHz stereo float PCM input and H.264/AAC MP4 output.
- Configurable rates are 30/60/120 FPS, respectively owning 1600/800/400 audio sample frames. Gameplay/audio advance in 50-sample intervals; one texture is rendered per video frame. Immutable `VideoRecordingOptions` also carries target bitrate, H.264 profile and output dimensions (720p/1080p/1440p/2160p in the client).
- Replay time derives from cumulative output sample positions and the constant mod rate. Wall-clock interpolation is disabled. Variable-rate mods are rejected.
- The worker owns GPU, audio, scene and encoder operations, including teardown. Live textures, draw nodes and audio channels must never enter this context.
- Native encoding converts GPU frames to NV12 and uses hardware H.264. Each offline frame reaches the encoder one frame late, once its conversion's GPU query has completed, so the export worker does not wait for every frame's own GPU work; `Complete()` flushes the last frame through `g0_record_offline_flush`, which the constructor also calls once so an outdated native library fails before rendering. Query waits spin and yield before sleeping. Pool pressure waits on the export worker; completion checks accepted-frame/drop statistics. FFmpeg uses pipelined readback (three staging textures, read two frames after submission), a dedicated pipe-writer thread bounded to three frame buffers, libx264 and a temporary PCM file. Readback copies mip level 0 only, since framebuffer textures carry mip levels. FFmpeg failures report its exit code and the end of its standard error.
- The client writes a unique `.partial.mp4`, finalises it, then moves it to the destination without overwrite. Cancellation/failure removes the partial file. The framework encoder alone does not own this publication policy.
- Resources are disposed in reverse dependency order. Queued audio disposal is processed before the private mixer is released; GPU disposal is drained after the private device becomes idle.

## Live capture and streaming

`DesktopVideoRecording` separately captures the live D3D11/Vulkan renderer and BASS mixer audio on a bounded worker queue. It supports either an MP4 sink or an encoded-packet sink. `RtmpVideoPublisher` normalises H.264/AAC into FLV messages and sends RTMP or certificate-validated RTMPS.

Network work stays off encoder callbacks. Compressed buffering is bounded by packet count, bytes and media duration. Congestion discards dependent GOPs and requests an IDR. Reconnection re-emits codec configuration. Publishing addresses are memory-only and transport errors are sanitised.

The client makes recording and streaming mutually exclusive. There is no simultaneous file/network fanout. Offline export remains independent of live capture.

## Dependencies and limits

See `../native/gpu-video/README.md` for build-only commands and native prerequisites, and the sibling client's `docs/video-recording.md` for UI usage.

- Native encoding is Windows-only and requires a suitable Media Foundation hardware H.264 encoder. FFmpeg replay export targets Windows/Linux/macOS desktops with libx264/AAC and an available private graphics backend; Linux/macOS runtime validation remains outstanding. Live recording/streaming still requires Windows.
- Live audio uses `AudioManager.BeginCapture()` with fixed 44.1 kHz stereo float output. WASAPI uses the deployed native PCM tap, with format conversion on a dedicated capture worker and a bounded two-second PCM ring and no managed recording DSP on the playback graph. Other backends use BASS DSP taps. Device restarts preserve capture intent and the output timeline; gaps become silence. Live video encoding remains Windows-only. Offline audio retains its private 48 kHz mixer and does not subscribe to live device changes.
- Vulkan capture requires the local Veldrid interop API, matching adapter capabilities and explicit opt-in before startup.
- General game settings, selected skin and ruleset configuration are snapshotted. Ruleset configurations are restored into private instances; custom configuration managers without snapshot support are rejected. Variable-rate mods are unsupported.
- The client provides a sample-position-driven presentation clock for cursor trails. It measures output time independently of beatmap playback rate and real rendering speed; live gameplay clocks and skin fallback rules are unchanged. Visual/runtime validation remains pending.
- Storyboard video decode readiness under faster-than-real-time simulation, complete replay fidelity, long-session AV drift, encoder/device loss and resource lifetime under repeated exports require runtime acceptance.
- The client still references the published framework package `2026.1011.2`, which lacks these new APIs. A framework release and then a client dependency update are required. Local project references used for builds must not be committed. Resource translations likewise need their normal authorised release.

## Verification scope

Joint managed and native builds passed. Tests, probes and external streaming connections were not run. Complete replay export, cross-platform behaviour and streaming interoperability remain unverified. Historical standalone results do not establish acceptance of the current integration.

### FFmpeg readback performance

Release headless benchmark, 1080p60 autoplay, 900 frames, with an executable standing in for FFmpeg that only drains the pipe (so libx264 encoding time is excluded). Mean per-frame time after the first second: render with a GPU wait per frame 3.8–4.0 ms; synchronous readback 8.5–8.9 ms; pipelined readback 4.4–6.1 ms (six runs, about 5.0 ms on average). Before the mip-level fix the FFmpeg backend failed on its first frame. A real FFmpeg/libx264 export has not been measured on this machine.

### Media Foundation offline performance

Same benchmark with the native encoder (1080p60, 900 frames, five alternating runs per library, both built locally with Zig 0.16.0). Mean per-frame time after the first second:

| Native library | Mean | p95 |
| --- | --- | --- |
| Synchronous conversion wait polled with `Sleep(1)` | 15.5–15.6 ms | 15.8–16.1 ms |
| Deferred hand-off, spin-then-sleep wait | 3.4–4.1 ms | 5.1–5.8 ms |
| Reference: render and submit, no GPU wait | 3.5 ms | 5.7 ms |

The old figure tracks the Windows timer tick (15.6 ms by default): `Sleep(1)` lasted a whole tick, so the result depended on whether another process had raised the timer resolution (an earlier run measured 4–5 ms). The deferred path now matches the CPU-bound floor. The 900-frame output decoded with `verify.exe`: 900 frames, last timestamp 14.983 s, audio present. The live capture path shares the new query wait but was not run.
