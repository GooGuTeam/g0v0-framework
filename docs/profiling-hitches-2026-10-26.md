# Profiling: hitches and the offline render benchmark (2026-10-26)

Follows `profiling-bottlenecks-2026-10-26.md`, which covered steady-state CPU cost.

## Offline render benchmark

`osu.Game.Tests/Visual/Gameplay/TestSceneOfflineRenderBenchmark.cs` (explicit) renders an autoplay replay of the test beatmap through the replay export path (`OfflineReplayExportSession`, `OfflineRenderContext`). The encoder is replaced through an internal constructor hook by a recorder that only calls `GraphicsDevice.WaitForIdle()`. Each frame therefore includes scene updates, draw submission and GPU work, without vsync, frame limiters or window state, and the replay is deterministic.

- Environment: `G0V0_BENCH_FRAMES` (default 1800), `G0V0_BENCH_LABEL`. Output: mean, p50/p95/p99/max after the first second, the first second's maximum, and a per-frame CSV in the temp directory.
- Cases: 1920×1080 and 2400×1080 (phone-like), D3D11 offline device.
- The first case runs in a cold process and carries most JIT and shader compilation; the second reuses warmed code. Compare like with like.
- Caveat: the offline device maps vertex staging buffers far more expensively than the live renderer (about 0.12 ms versus 0.004 ms per frame), likely because every frame waits for the GPU. Treat offline frame times as comparative, not absolute.

Example (Release, RTX 5060 Laptop):

| Case | mean | p50 | p95 | p99 | max | first-second max |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1920×1080, cold process | 2.5–2.9 ms | 2.3–2.5 | 4.8–6.5 | 8.9–10.6 | 40–80 | 240–430 |
| 2400×1080, warm process | 1.1–1.2 ms | 1.0 | 1.8–2.0 | 2.8–3.1 | 12–49 | 185–220 |

Ranges are across three runs; run-to-run variance is large in the cold case.

## Hitch sources

1. **Shader compilation on first use, inside a frame.** `VeldridShader.loadToGpu` runs from `Bind` (`EnsureShaderInitialised`) or a scheduled task in `BeginFrame` the first time a shader is used. In a cold benchmark trace this totalled about 0.5 s: 247 ms from texture-shader binds, 62 ms from the cursor trail. D3D11 compiles HLSL at runtime (`D3D11Shader.compileCode` also appears in live song-select traces). `ShaderCompilationStore` caches the cross-compiled source, not backend bytecode. Candidates: compile known gameplay shaders during loading (player loader), and persist backend binaries (DXBC/DXIL, Metal libraries, `glGetProgramBinary` on GL/GLES) keyed by the existing source hash. On Android every first play currently pays the driver's GLSL compile.
2. **First-call JIT in a cold process.** Cold-run p99 is about three times the warm run's, and a gameplay-start frame spent 78 ms in `KeyBindingContainer.PropagatePressed` → LINQ `FirstOrDefault`. Desktop Release builds do not enable ReadyToRun (`PublishReadyToRun` is not set in `osu.Desktop` or the release scripts). Android Release has no explicit AOT settings (see the IL2CPP discussion). Candidates: ReadyToRun for desktop publishes; profiled AOT on Android with a profile recorded from menus and gameplay.
3. **Synchronous drawable loading while scrolling song select.** A 73 ms update frame in `TestSceneBeatmapCarouselScrolling` loaded a new drawable synchronously (`AddInternal` → `load` → dependency injection) on the update thread. Candidate: identify the panel subcomponent and move it to async loading or pool it.
4. **Test-only:** `TestBrowser` scans assemblies for test types (`RuntimeModule.GetTypes`, 0.3–0.7 s). Not present in the game.

Draw-thread hitches could not be identified from live sampling: with a focused window and VSync, consecutive samples stay inside `DrawFrame` across many normal frames. The offline benchmark avoids that ambiguity for gameplay.

## Fixed during this investigation

**Replay export disposal failure.** Disposing the export scene ran `OsuVisualOrdering.Dispose`, which removes proxies from a loaded container. The export worker was not flagged as the scene's update thread during disposal, so it threw `InvalidThreadForMutationException`. `ReplayExportJob` would then report failure after `CompleteAsync` had already published the video. `OfflineRenderContext.DisposeScene` now disposes a scene with the export thread acting as its update thread, as `UpdateScene` and `Render` already do. `OfflineReplayExportSession` disposes its scene through it, in the same reverse-ownership order. Verified with the benchmark, which completes without disposal errors.

## Not verified

- Live draw-thread hitches; GPU timing on non-D3D11 backends; Android and iOS devices.
- The benchmark's offline device differs from the live renderer in buffer mapping cost.
