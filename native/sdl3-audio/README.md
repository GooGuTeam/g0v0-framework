# SDL3_mixer native audio prototype

Standalone migration spike, NOT a replacement for AudioManager/TrackBass/SampleBass yet. BASS behavior and production packaging are unchanged. Bridge is C; SDL owns the native WASAPI device/mixing thread. No managed callbacks are installed. C# smoke tool issues serialized control calls on its main thread. SDL requires device mixer creation/destruction on the main thread; do not initialize this from framework AudioThread.

## Dependencies and build

Validated against official SDL 3.4.18 and SDL3_mixer 3.2.4 Windows x64 VC development archives, compiled using Zig 0.14.1's C compiler. Downloads/toolchain live outside the framework tree in migration-audit. SDL and SDL_mixer are zlib licensed; optional codecs have their own licenses (e.g. libgme LGPL). This prototype only copies the core DLLs. A production build must audit enabled decoder implementations, pin dependency versions and retain their notices. Do not overwrite the game's existing SDL3.dll: its binding/native version compatibility must be verified first.

From workspace root:

```powershell
./g0v0-framework/native/sdl3-audio/build-win.ps1 `
  -Zig ./migration-audit/native-toolchain/ziglang/zig.exe `
  -SdlSdk ./migration-audit/sdl3-mixer-sdk/SDL3-3.4.18 `
  -MixerSdk ./migration-audit/sdl3-mixer-sdk/SDL3_mixer-3.2.4 `
  -Output ./migration-audit/sdl3-audio-bin

dotnet build ./g0v0-framework/tools/Sdl3AudioSmoke/Sdl3AudioSmoke.csproj -c Release
Copy-Item ./migration-audit/sdl3-audio-bin/*.dll ./g0v0-framework/tools/Sdl3AudioSmoke/bin/Release/net10.0/
dotnet ./g0v0-framework/tools/Sdl3AudioSmoke/bin/Release/net10.0/Sdl3AudioSmoke.dll <audio-path> 256 wasapi gc-stress
```

Use 128/256/512 frame requests for comparison. Windows buffer size is negotiated, not guaranteed. The tool prints actual device frames. At 48kHz, 256 frames is about 5.33ms per buffer, NOT total output latency.

## Real-time policy

- Force WASAPI only in the standalone Windows test; allow default selection via the tool's `default` argument.
- The reviewed SDL 3.4.18 WASAPI implementation calls AvSetMmThreadCharacteristicsW with `Pro Audio` for its device thread, and reverts it on teardown. Registration can fail; this prototype does not claim to expose/verify actual MMCSS status.
- Do not use REALTIME_PRIORITY_CLASS, fixed CPU affinity, timeBeginPeriod, spin loops, or a second polling audio thread.
- All audio is predecoded at load time outside the callback. This trades memory for avoiding decoder/file work during playback. Long songs need a separately designed native decode worker and bounded PCM ring buffer; not a managed stream callback.
- Callback telemetry uses only an SDL atomic counter. It does not allocate, log, perform disk I/O or call CLR. SDL_mixer itself may allocate/lock internally; hard real-time/allocation-free behavior has NOT been established.
- Control operations call SDL_mixer directly and may contend with mixer locks. They are not a lock-free realtime command queue. Preload before gameplay; do not load/destroy assets during latency-critical playback.
- A CLR GC cannot directly suspend native mixing execution here, but CPU/memory pressure can still disrupt deadlines, and delayed C# commands still mean delayed hitsounds.

## Implemented

Fixed capacity of 128 track/audio pairs, UTF-8 file loading with full predecode, play/stop, gain, frequency ratio, seek, source-position polling, native callback telemetry, negotiated device buffer inspection and synchronous teardown. One loaded pair is one voice; polyphony requires multiple tracks. No stale-handle generation or asynchronous ownership protocol yet; serialize all calls and never use handles after destruction.

Frequency changes alter pitch and speed together, NOT pitch-preserving Tempo. Playback position is the source cursor, NOT DAC/heard position; it must not replace TrackBass.CurrentTime without a calibrated clock design.

## Missing before framework integration

- SampleStore/TrackStore adapters, memory resource loading, pooled polyphonic voices and channel lifetime.
- Aggregate gain/pan/rate and mixer routing semantics.
- Pitch-preserving tempo, reverse playback, waveform/FFT.
- Output-clock estimation, latency accounting, seek/loop timing and rhythm-game synchronization.
- Pause/resume, device enumeration, hotplug, failure recovery and shared SDL initialization ownership.
- Native streaming worker for long tracks; bounded command transfer if mixer lock contention is measurable.
- Safe managed ownership wrapper, deployment automation and platform CI.

Fail unsupported functionality explicitly; do not silently treat tempo as frequency or source cursor as presentation time.

## Validation

Native compilation with -Wall -Wextra -Werror succeeded. Managed Release smoke tool built with 0 warnings/errors. A generated quiet 3-second WAV ran on the local Windows WASAPI device with requestedFrames=256, actualFrames=256. Native callback count advanced 96 -> 284 while the managed control thread slept and forced compacting GC; source cursor reached 1.504 seconds. Seek, frequency change and stop returned success and teardown completed.

This is a smoke check, not an underrun/latency measurement or gameplay parity result. No claim of audible glitch-free playback. Next: long duration stress, MP3/Vorbis format checks, concurrent hitsounds, device disconnect, ETW/WPR scheduling analysis and external loopback latency measurements.
