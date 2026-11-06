# Profiling: remaining bottlenecks (2026-10-26)

## Method

- Release builds of `osu.Game.Tests` and `osu.Game.Rulesets.Osu.Tests` against local framework, resources and Veldrid sources (`build/SourceDependencies.targets`), including this session's masking retention, untextured white pixel and regional mipmap changes.
- Scenes: `TestSceneSongSelect` (after `SoloSongSelect` was entered) and `TestSceneOsuPlayer` (standard test beatmap, steady play).
- `dotnet-trace` 10.0.750501, 15-second windows: `dotnet-sampled-thread-time` (about 100 Hz managed stack sampling), `gc-verbose` (GC events, allocation ticks), and a separate capture of runtime contention events (keyword `0x4000`). Speedscope exports were aggregated per thread with scratch scripts; `.nettrace` files were parsed with the TraceEvent library shipped with `dotnet-trace`.
- Configurations: default (D3D11, multi-threaded, VSync); D3D11 multi-threaded with `FrameSync = Unlimited`; and **OpenGL single-threaded** with `FrameSync = Unlimited`, which is the Android renderer and execution mode. The test window was unfocused, so the host's inactive cap (`GameThread.DEFAULT_INACTIVE_HZ` = 60) applied. With about 900 frames per window, busy time converts to per-frame cost.
- Machine: Windows 11, RTX 5060 Laptop GPU, 20-core CPU. Numbers are desktop CPU time; mobile CPUs are several times slower.

## Frame budget

| Scene / configuration | Update | Draw | Swap / present |
| --- | ---: | ---: | ---: |
| Song select, D3D11 multi-threaded | 0.79 ms | 0.50 ms | 0.16 ms |
| Gameplay, D3D11 multi-threaded | 0.32 ms | 0.42 ms | 0.19 ms |
| Gameplay, OpenGL single-threaded | 0.60 ms | 0.41 ms | 0.07 ms |

In single-threaded mode, update and draw run back to back on one thread, about 1.0 ms per frame on this CPU. A phone CPU several times slower puts this at a significant fraction of an 8.3 ms (120 Hz) budget before GPU time.

- **GC:** one gen-0 collection in 15 s of gameplay, none in song select; sampled allocation about 0.2 MB/s. Not a bottleneck. (Suspension totals in these traces are dominated by the sampling profiler itself.)
- **Lock contention:** zero contention events in 15 s of gameplay. `Monitor.Enter_Slowpath` samples (for example in `InputManager.GetPendingInputs` via the tablet handler's `filterSync`) are slow-path acquisitions and sampler bias, not waiting. Disabling the tablet handler removed those samples, but no thread was found holding the lock. Not treated as a finding.

## Findings, ranked by measured share

Percentages are of the given thread's busy time in the OpenGL single-threaded gameplay profile unless stated. The test browser adds its own drawables (step list, frame recording container), which slightly inflates tree-wide costs.

### Update

1. **Per-frame child lifetime scans: about 17% of update.** `CompositeDrawable.CheckChildrenLife` iterates every child of every composite each frame to re-evaluate `ShouldBeAlive`, almost all self time. Most UI children have infinite lifetimes and are already loaded. Candidate: track per composite whether any child has a finite lifetime or pending load, and skip the scan otherwise; maintain the count via lifetime changes and child add/remove. Must preserve load-on-alive, `RemoveWhenNotAlive` and `LifetimeManagementContainer` semantics.
2. **Input queue construction: about 8% (positional) plus about 2% per non-positional queue.** Three `KeyBindingContainer`s (`GlobalAction`, `PlatformAction`, `FrameworkAction`) rebuild positional queues from `InputManager.Update` and again from `HandleMousePositionChange`. Candidate: reuse the positional queue within a frame when the tree's input-relevant invalidation state and mouse position are unchanged.
3. **Cursor trail snapshotting: about 7%.** `CursorTrail.TrailDrawNode.ApplyState` plus `Buffer.Memmove` copy the 2048-slot array every frame. This is client code (`osu.Game.Rulesets.Osu`), already analysed in `rendering-next-steps-research.md` §2 (incremental snapshots).
4. **Transforms: about 8%** (`TargetGroupingTransformTracker.UpdateTransforms`). Expected for gameplay; no specific waste identified.

### Draw

5. **Batch flushes: 45% of draw; flushes triggered by masking changes: 18%.** Per-flush cost on GL is mainly `glBufferSubData` (`GLVertexBuffer.UpdateRange`, 13.6%) and the draw call. On D3D11, resource set activation from masking-triggered flushes is the largest share of `SetGraphicsResourceSet`. This is the draw-call count problem. The remaining lever is per-primitive mask data (vertex mask index plus mask storage), see `masking-interior-retention.md`; implemented in `per-vertex-masking.md`.
6. **GL vertex buffer churn: about 8% of draw.** `GL.GenVertexArray` (4.3%), `GL.GenBuffers`, and `PooledMemoryOwner` construction with cleared memory (inside `TryAddQuad`) occur in steady gameplay. `Renderer.freeUnusedVertexBuffers` frees a vertex buffer, its VAO and its pooled CPU memory after `RESOURCE_FREE_NO_USAGE_LENGTH` = 300 **frames** without use. Composites that draw intermittently with their own batches (pooled hit objects, judgements, follow points) cross that threshold and recreate everything later. The threshold is about 5 s at 60 Hz and about 1.25 s at 240 Hz. Candidates: a time-based threshold, retaining a small free list of buffers by size, and avoiding clearing on re-rent where the vertices are fully overwritten before upload. Veldrid vertex buffers follow the same policy and were not separately measured.
7. **Cursor trail flush: about 4.6% of draw** (attributed to `Renderer.PopProjectionMatrix` from `CursorTrail.TrailDrawNode.Draw`, which flushes the trail batch). Client side; follows from item 3.
8. **D3D11 present path: 44% of the draw thread** (`GameHost.Swap`: `FinishCommandList`, submission, present). Driver-side work proportional to recorded commands. It goes down with draw-call count, not with framework code changes in isolation.

Minor or not actionable: `Path` uniform buffers are cached per draw node (`??=`) and are created only when new slider paths appear; `IASetVertexBuffers` is already skipped by Veldrid when bindings are unchanged and fires only on actual batch switches.

## Fixed during this investigation

**GL renderer crash with single-channel framebuffers.** The existing uncommitted change that added an upload/texture format check in `GLTexture.Upload` made `GLFrameBuffer` throw `Texture upload format R8G8B8A8Float does not match texture format R8Float`. Both its texture constructor and its `Size` setter used a format-less `new TextureUpload()`. Gameplay (which uses a single-channel buffer) crashed within seconds on the OpenGL renderer, the only renderer on Android. `GLTexture.CreateEmptyUpload()` now mirrors `VeldridTexture.CreateEmptyUpload()`, and `GLFrameBuffer` uses it in both places. Verified by the OpenGL single-threaded gameplay run (no unhandled errors) and the explicit `GLFrameBufferFormatTest` (R8 and RGBA framebuffers create and resize).

## Limitations

- Desktop only; no Android or iOS profiling. OpenGL single-threaded on desktop exercises the same framework code paths as Android, not the same drivers or CPU.
- Sampled thread time is biased towards threads in preemptive mode; shares are indicative. GPU time was not measured.
- 15-second windows of single scenes in the visual test browser, not the full client with real beatmaps and skins.
