# Experimental native WASAPI PCM tap

When deployed, this bridge supplies the native WASAPI callback while retaining the BASS mixer and device output. It calls BASS_ChannelGetData exactly once and copies the produced interleaved float PCM into a preallocated native SPSC ring. Full/oversize blocks are dropped from capture, never waited for; device output is unchanged. Disabled capture does not copy PCM. Callback work includes no CLR transition, allocations, locks, logging, resampling or encoding.

## Build and select

```powershell
../build-recording.ps1 -Zig C:/path/to/zig.exe -VulkanHeaders C:/path/to/Vulkan-Headers -DeployDirectory C:/path/to/client/output
$env:G0V0_WASAPI_CAPTURE_LIBRARY = (Resolve-Path ../artifacts/win-x64/g0v0-wasapi-capture.dll).Path
```

Start a locally built framework/game with WASAPI enabled. The application-local DLL is selected by default when deployed; the optional environment variable overrides it with an absolute path to a trusted DLL. Invalid optional libraries preserve the normal playback bridge, but WASAPI recording audio is unavailable. The old `build-win.ps1` executes probes and must not be used for build-only validation.

On a worker/control thread, use `NativeWasapiCapture.SetEnabled(true)` and repeatedly `TryRead` with a reusable buffer of 65536 bytes. `SetEnabled` returns false unless the native bridge has been configured. Device changes pause capture and increment the generation; capture intent is reapplied before the new producer starts. Exactly one consumer should own the stream (managed calls are serialised for teardown safety, but multiple readers would split blocks).

Block metadata: sample-frame position, QPC at mixing completion, byte length, frequency and channel count. Positions include dropped/disabled intervals. QPC is NOT the loudspeaker presentation time. The client consumer preserves a recording timeline across generation changes and converts formats on a dedicated capture worker with a bounded two-second PCM ring. The reader drains independently of encoding, with a 2 ms wait between bounded drains; scheduling/GC stalls can still overflow the native queue. Precise AV synchronisation and physical restart continuity remain unverified. Queued blocks can remain after disabling; they retain their original positions.

The ring holds 32 blocks, each at most 64 KiB. It uses lock-free atomic indices and release/acquire publication. Pages are touched before playback. Runtime `enabled` changes are non-blocking and may take effect on the next callback. Configure/destroy require callback quiescence. AudioThread now stops/frees WASAPI before resetting/freeing the mixer; final capture teardown occurs after devices stop. This ordering still needs physical device-loss/hotplug stress testing.

## Historical validation (not rerun for current integration)

- Zig 0.14.1 native x64 DLL/test compiled with `-O2 -Wall -Wextra -Werror`.
- Native passthrough, disabled state, queue-full/oversize overflow, explicit position gaps, short consumer buffers, BASS error, unsigned index wraparound, and 100000-block two-thread SPSC test passed.
- Framework build with warnings-as-errors passed.
- Managed ABI-size test and explicit native interop/device-generation reset test passed. The latter uses a fake mixer callback only for the test; production resolves BASS directly.

A physical default-device WASAPI-exclusive smoke test was attempted and failed at initialisation with `Bass.LastError = Busy`, before callbacks started. It is blocked on releasing the endpoint from other software; no process was stopped and no audio settings changed. No physical exclusive capture or latency measurement has passed. The current client connects the tap to recording/streaming, but the historical checks do not validate that integration. ARM64/x86 builds and realtime latency under memory pressure are unverified.
