# Android performance research (2026-10-27)

## Scope and method

Earlier performance work (`profiling-bottlenecks-2026-10-26.md`, `per-vertex-masking.md`, `shader-binary-cache-and-warmup.md`, `fragment-shader-mobile-optimisation.md`, `region-mipmap-generation.md`) was measured on desktop and lists Android as not verified. This document covers the Android-specific layer: `osu.Framework.Android`, the Android paths in `GameHost` and the legacy GL renderer, the `osu.Android` head, and the packaged Release APK.

- Static review of the code paths below.
- Inspection of the local Release APK installed on the test device (`/data/app/top.g0v0.lazer-*/lib/arm64`) and of the device's game logs and `framework.ini`.
- arm64 profileable Release builds on the device with the OpenGL renderer. They stopped at startup until the GLES link fix (see "Fixed: GLES programs failed to link"); short Autoplay runs then compared Mono and CoreCLR (finding 2).
- Slider timing of 132 osu!standard beatmaps from a local osu! installation, replayed at 60 Hz with the snaking rules of `SnakingSliderBody`.

Test device: Xiaomi MIX 2S. Snapdragon 845 (4× Cortex-A75 + 4× Cortex-A55), Adreno 630, OpenGL ES 3.2 driver V@415 (2020-09), Vulkan 1.1.87, Android 10 (API 29), 1080×2160 at 60 Hz only, 8 GB RAM. `simpleperf` and `perfetto` are on the device; Adreno `kgsl` sysfs counters need root.

## Current state

| Area | State |
| --- | --- |
| Renderer | Legacy `GLRenderer` on a GLES 3.0 context (the driver provides 3.2). Veldrid Vulkan is selectable through `framework.ini`; on this device the last six Vulkan runs failed at the first graphics pipeline creation (`VkResult: Incomplete` from `VkPipeline..ctor`), then on every `CommandList.Begin`. |
| Execution mode | Default `SingleThread` (`AndroidGameHost.cs:40`). The device's config has `MultiThreaded`. |
| Frame limiter | Default `Limit2x` on every platform (`FrameworkConfigManager.cs:43`). On mobile, update is capped at twice the refresh rate (`GameHost.cs:1453`); draw is not. |
| Presentation | GL: `eglPresentationTimeANDROID(now)`, swap, then a Choreographer wait when VSync is on (`AndroidGameHost.cs:53-65`). |
| Render scale | Offscreen framebuffer with a D16 depth buffer, then a full-screen blit (`GameHost.cs:539-606`). The device uses 0.8. |
| Window surface | RGBA8888, depth 16, stencil 8 (`SDL3GraphicsSurface.cs:40`). |
| Front-to-back pass | Bypassed by default (`FrameworkDebugConfig.cs:19`, upstream commit `6f4fa78b3`); the setting is not persisted and can be switched in Settings → Debug. |
| Runtime | CoreCLR with JIT, arm64 only, API 24+, since upstream's switch was ported to the g0v0 working tree on 2026-10-28 (uncommitted; finding 2). Before that: MonoVM. Release defaults to profiled AOT with the SDK's generic startup profile (`Microsoft.Android.Sdk.DefaultProperties.targets:124`), which the CI build (`.github/actions/android-build.sh`) uses: a local build with defaults has 15 MB of AOT images in total, 2.5 MB of them for `osu.Game`, and Mono's JIT compiles the rest at run time. The APK installed on the test device was built with full AOT (159 images, 225 MB, 47 MB for `osu.Game`). Upstream osu! moved Android to CoreCLR on 2026-10-15 (finding 2). |
| Shader binaries | GL program binary cache implemented; not verified on a GLES driver. |
| Texture probe | Adreno 630 reports 16384; single-channel font atlas pages are 8192 (64 MiB each). |
| Platform hooks | No thread priorities, no ADPF performance hints, no Game Mode, no `android:appCategory="game"`, no sustained performance mode, no `requestUnbufferedDispatch`. |
| APK (local build) | 274 MB, of which the assembly store is 211 MB. It contains the Khronos validation layer (27 MB, from `G0V0AndroidVulkanLayer`) and AOT images of desktop-only assemblies. |

## Fixed: GLES programs failed to link

Fixed in the working tree on 2026-10-28 (uncommitted). Before the fix, with the OpenGL renderer on the device, every build stopped during startup:

```
GLShader failed to link: Texture3D/Texture: Error: input vdspv_fsin5 not declared in output from previous stage.
GLShader failed to link: CursorTrail/Texture: Error: input vdspv_fsin7 not declared in output from previous stage.
```

`GLShader` adds per-program defines to the fragment source (`G0V0_PER_VERTEX_MASKING` adds inputs at locations 5 and 6, `G0V0_TEXTURE_SLOTS` at location 7) only when the program's vertex shader writes them. `GLShaderPart.Compile` compiled a part once and ignored later calls, and parts are shared by name through `ShaderManager.partCache`. The first program that uses `sh_Texture.fs` (`Texture2D/Texture`, with both defines) therefore fixed the compiled fragment shader for every later program, including `Texture3D/Texture` and the game's `CursorTrail/Texture`, whose vertex shaders write neither input. The Adreno GLES linker rejected the mismatch; desktop drivers accepted it.

The fix: `GLShader` compiles its own vertex and fragment shader objects from its final text, links, and deletes them; `GLShaderPart` only holds the preprocessed text and no longer owns GL objects. Each program now compiles its fragment stage separately, which costs more compilation on a cold program binary cache. The binary cache key moved to version 2 (`GLRenderer.GetProgramBinaryKey`), so that binaries linked against another program's stage on tolerant drivers are not reused. On the MIX 2S the CoreCLR Release build starts on GL and plays Autoplay with no link or compile failures and no errors in the runtime log, on a cold cache and on the next launch.

## Findings

Ranked by expected effect and confidence. None is measured on a device yet.

### 1. Twice as many frames are drawn as a phone displays

`Limit2x` turns VSync off, which sets the EGL swap interval to 0 (`SDL3GraphicsSurface.cs:169`). Android then puts the window's buffer queue into async mode: a queued frame that has not been latched yet is replaced by the next one. At 60 Hz the limiter draws 120 frames per second and about half are discarded before display. CPU and GPU work, power and heat double for a latency gain of at most half a refresh. Thermal throttling on a phone then costs displayed frames later in a session.

The presentation timestamp carries no target: it is the time of the swap, which matches the default. Frame pacing libraries (Swappy) set it to the intended vsync from Choreographer's `frameTimeNanos`, which keeps the queue short without dropping frames.

Candidates:
- On mobile, cap draw at the refresh rate (VSync with Choreographer pacing, or a 1× limiter) while update stays at 2× in multi-threaded mode.
- Pass Choreographer's next vsync time as the presentation timestamp.
- Time touch input by its OS event time, as osu!droid does (see "Reference: osu!droid"), so the update rate stops limiting judgement precision and draw and update can both run once per refresh.

Measure input-to-display latency and sustained frame rate after 10 minutes before changing the default.

### 2. CoreCLR replaces Mono's code generation

Shipped builds run on MonoVM with profiled AOT: only methods in the SDK's generic startup profile are precompiled, and Mono's JIT compiles the rest, including the gameplay loop (see "Current state"). Every desktop profile so far ran on CoreCLR, so part of the gap between desktop and phone frame times is Mono code generation.

**Upstream.** [ppy/osu#39029](https://github.com/ppy/osu/pull/39029), merged 2026-10-15 and still on master, moved osu! Android to CoreCLR on .NET 10:

- `UseMonoRuntime=false` and `PublishReadyToRun=false` (JIT only), `RuntimeIdentifiers` reduced to `android-arm64`, `SupportedOSPlatformVersion` and every manifest's `minSdkVersion` raised from 21 to 24, `XA1040` (experimental runtime) suppressed, `AndroidHttpClientHandlerType` removed.
- `HighPerformanceSessionManager` moved from osu.Desktop to osu.Game and cached in `OsuGameAndroid`, so gameplay runs with `GCLatencyMode.LowLatency` on Android too.
- `RulesetStore` checks `RuntimeInfo.StartupDirectory` with `string.IsNullOrEmpty`: `AppContext.BaseDirectory` is an empty string on CoreCLR for Android instead of null, and the old null check made it try to load rulesets from disk and show an error at startup. g0v0 has the same check (`RulesetStore.cs:75`); it is the only Android-reachable use of the base directory in the game and framework.
- Reported results, without frame-time measurements: song select went from about 50 fps with dips to 60 fps with dips on one device in Release (maintainer). A contributor measured ReadyToRun cutting startup from 5 s to 3 s at the cost of 40 MB more APK and 100 MB more installed size, and a Java GC about every two minutes with default GC settings but almost none during gameplay with `LowLatency`.

**g0v0 offline build.** The current working tree builds for CoreCLR without source changes: `-p:UseMonoRuntime=false -p:PublishReadyToRun=false` with the arm64 Release command from the measurement plan. The build accepts `minSdkVersion` 21 with only the `XA1040` warning, although the runtime requires API 24. Not run on a device.

| arm64 APK | Mono, profiled AOT (defaults) | CoreCLR, JIT (upstream settings) | CoreCLR, ReadyToRun (SDK default) |
| --- | ---: | ---: | ---: |
| APK file | 214.2 MB | 213.9 MB | 251.1 MB |
| Uncompressed contents | 299.9 MB | 295.1 MB | 340.3 MB |
| Assembly store (uncompressed) | 201.5 MB | 201.6 MB | 246.8 MB |
| Precompiled code | 15.4 MB AOT images | none | in the assembly store |

The assembly store (179 MB compressed, mostly `osu.Game.Resources`) dominates all three. CoreCLR replaces `libmonosgen-2.0.so` (3.0 MB) and the AOT images (15.4 MB uncompressed) with `libcoreclr.so` (5.9 MB), `libclrjit.so` (3.0 MB) and the debugger libraries `libmscordaccore.so` and `libmscordbi.so` (4.4 MB).

**Known risk: Java GC bridge stalls.** When a CoreCLR collection finds managed objects that reference Java objects, dotnet/android runs a Java `Runtime.gc()` on a dedicated thread that has been idle, so it starts at a low CPU frequency: about 21 ms per ART collection against 10 ms on Mono on a Galaxy S23, which a MAUI game saw as 6–10 dropped frames every few seconds ([dotnet/runtime#131370](https://github.com/dotnet/runtime/issues/131370), milestone .NET 12; [dotnet/android#12236](https://github.com/dotnet/android/issues/12236), [#12436](https://github.com/dotnet/android/issues/12436)). A fix for the runtime waiting on the Java collection is in .NET 11 previews. The documented workaround is a larger gen0 budget, which makes collections rarer: `DOTNET_GCgen0size=128000000` and `DOTNET_GCGen0MaxBudget=128000000`, which the build can set in Settings > Flags..., then restart the game. osu! holds few Java objects (the SDL activity and a few platform services), so each bridge round should be small; the contributor's observation of a Java GC every two minutes fits that.

**Ported to g0v0 on 2026-10-28 (uncommitted):** `osu.Android.props` (`UseMonoRuntime=false`, `PublishReadyToRun=false`, `android-arm64` only, `SupportedOSPlatformVersion` 24, `XA1040` suppressed), `minSdkVersion` 24 in all six Android manifests, the `RulesetStore` check, and `HighPerformanceSessionManager` moved to osu.Game and cached in `OsuGameAndroid`; the moved and edited files match upstream apart from the licence-file line. `AndroidHttpClientHandlerType`, which upstream also removed, is kept. The Release configuration still builds one APK per ABI, now only arm64, and `.github/actions/android-build.sh` and the README list only `g0v0.apk`; `android-arm` returns in .NET 11. The Release APK builds from the props alone (arm64, `libcoreclr.so`, no AOT images, `minSdkVersion` 24) and, with the GLES fix, starts and plays on GL on the MIX 2S.

**Device A/B (MIX 2S, 2026-10-28).** Every arm was built from the same working tree (arm64 Release, profileable) with two temporary patches, reverted afterwards: per-program GL shader objects, without which every build stopped on GL (since fixed, see "Fixed: GLES programs failed to link"), and upstream's `RulesetStore` check. Settings: OpenGL, `FrameSync = VSync` so that both arms draw 60 frames per second and thread CPU compares equal work, and the device's other settings (multi-threaded, render scale 0.8). Workload: Renatus (Gamu) [Insane] with Autoplay, a 23 s window starting 12 s after the map starts, after one warm-up play per install.

| | Mono, profiled AOT (SDK default) | CoreCLR, JIT |
| --- | ---: | ---: |
| Presented fps | 59.7, 59.8 | 59.5, 59.8 |
| Frames over 20 ms (of about 1,250) | 5, 4 | 10, 5 |
| Update thread | 99%, 99% | 70%, 68% |
| Draw thread | 61%, 62% | 30%, 28% |
| Whole process | 193%, 194% | 132%, 127% |
| ART (Java) collections in the window | 0 | 0 |

CPU is the percentage of one core over the window, from `/proc/<pid>/task/*/stat`. At 60 draws per second, CoreCLR spends about 4.8 ms of draw thread CPU per frame against 10.2 ms on Mono. Mono's update thread is saturated, so it runs below its 120 Hz cap; CoreCLR does at least the same update work with 30% of a core to spare. Both deliver 60 fps at this setting, so the gain appears as CPU headroom. In the warm-up play each arm had 8 frames over 20 ms; the longest gap was 117 ms on Mono and 50 ms on CoreCLR. One warm-up window of Mono with full AOT was worse than profiled AOT (54.2 fps, 118 frames over 20 ms, update 99%, draw 77%), and that arm was not pursued.

CoreCLR leaves the update and draw threads without OS thread names (`Thread-12` and `Thread-5` instead of `Update (GameThread)` and `Draw (GameThread)`; identified here by creation order). Profiles from `simpleperf` and Perfetto need the same mapping.

**Still to measure on the device:** startup time, `Limit2x`, longer sessions and Java GC frequency, the gen0 budget environment variables, and ReadyToRun.

- **.NET 11 (GA expected November 2026):** CoreCLR is the default for Android from Preview 4 and the only runtime from Preview 6; the Mono opt-out property was removed. Android x86 and API 23 and lower are not supported.
- **What it changes:** RyuJIT code quality, tiered compilation with dynamic PGO (guarded devirtualisation of the interface and virtual calls that dominate drawable updates and draw node traversal), the CoreCLR GC, and `dotnet-trace` / `dotnet-counters` against the app on a device.
- **What it does not change:** GL driver time (buffer uploads, draw calls, swap) and GPU time, which findings 1 and 3–8 address.

LLVM for Mono AOT (`EnableLLVM`) and `MONO_GC_PARAMS` tuning lose their purpose with .NET 11 and are not worth pursuing. NativeAOT needs full trimming, which the reflection-based dependencies (Realm, AutoMapper, SignalR/MessagePack) make impractical.

### 3. Slider redraws split the main render pass

Tile-based GPUs (Adreno, Mali) render a framebuffer one screen tile at a time in on-chip memory and write it out when the pass ends. Binding another framebuffer in the middle of a frame ends the pass: the main target's colour and depth/stencil are written to memory, the offscreen pass runs, and the main target is read back before drawing continues. On desktop GPUs the same switch is a state change.

`BufferedDrawNode` redraws its framebuffer at its position in the draw tree. Slider bodies render their distance field into their own framebuffer (`slider-rendering-optimisation.md`) and redraw every frame while snaking in (the first third of the preempt time after appearing) and while snaking out (the final span), both on by default (`OsuRulesetConfigManager.cs:20-21`, `SnakingSliderBody.cs:124-139`). Song select captures have no offscreen pass with background blur off (the device's setting).

Replaying those rules over 132 osu!standard beatmaps at 60 Hz (1.12 million play frames, breaks excluded):

| | Median map | p10 map | p90 map |
| --- | ---: | ---: | ---: |
| Frames with at least one redraw | 57% | 46% | 69% |
| Frames with two or more | 13% | 8% | 19% |
| Redraws per frame | 0.70 | 0.55 | 0.87 |

The test set used by the offline benchmark (Soleily - Renatus) gives 63% and 0.81 per frame. The existing desktop draw captures agree: frame 300 of the benchmark has two slider redraws.

Each redraw costs the main target one store and one load of its colour, about 19 MB at 1080×2160 RGBA8 before compression. At 0.74 redraws per frame that is about 0.8 GB/s at 60 draws per second and 1.7 GB/s under `Limit2x` (finding 1), against about 30 GB/s theoretical bandwidth on the Snapdragon 845. The depth/stencil attachment only holds the frame-start clear (finding 4), which compressed formats store as metadata. Adreno's lossless framebuffer compression (UBWC) lowers the colour traffic as well; the driver may instead render the whole pass directly to memory, which moves all blending traffic off-chip. The GPU time this costs needs a device measurement.

Candidates:
- Render dirty buffered nodes in a pass before the main pass, and draw only their cached textures in the main pass. This keeps one main pass per frame on every backend.
- Slider direct rendering (`slider-direct-rendering-research.md`) removes the per-slider framebuffer. osu!droid draws its slider bodies as meshes in the main pass (see "Reference: osu!droid").
- Turning off snaking (an existing ruleset setting) removes nearly all redraws, at a visible cost.

**Desktop experiment: osu!droid-style slider bodies (`DirectPathRendering = true`).** `Path.DirectDrawNode.cs` draws a `SmoothPath` straight into the current target. Its gradient (the same bytes as the gradient texture, at texel centres) is split into bands with linear colour across distance, at most 0.125 colour change per band. Each band is a mesh in screen space, two quads per segment plus rounded joins and caps, drawn from the centre outwards with depth writes and `LessThan` at a depth unique to the path in the frame. Each pixel is written once, by the innermost band that covers it, so overlapping segments do not blend twice and no framebuffer, clear or composite is needed. Depths decrease per path from 1, so later paths draw over earlier ones. The `PathDirect` vertex shader (`sh_Texture2D.vs` with `G0V0_DRAW_DEPTH_ALWAYS`) applies the depth in framebuffers too; the offline export target gets a D16 attachment when the flag is enabled. The path falls back to its framebuffer when the target has no depth, depth testing is on (front-to-back pass), the colour is a gradient, or the path has no `DirectGradient`.

Results on desktop (offline benchmark, 1920×1080, Argon skin, Release, Veldrid):

- Pixels: in the slider regions of frames 120–900 the output matches the framebuffer path except at anti-aliased edges, by at most 20/255 and by more than 8/255 on about 0.2% of slider pixels; two baseline runs are identical there. The framebuffer path's edges are slightly softer because it resamples the distance field with linear filtering. The comparison found two bugs, now fixed: cracks between bands where each band chose its own arc subdivision, and a half-texel offset of the gradient.
- Draw calls: one per slider. Frame 262 has 36 draws instead of 38, with 5,884 vertices for two snaking sliders in place of a framebuffer redraw and composite.
- Frame time: no difference beyond noise. Paired rounds give median differences of +0.01 ms and +0.13 ms (5 and 8 rounds) on 3.6 ms frames. The mesh is rebuilt on the CPU every frame, which offsets what a desktop GPU saves; the gain this targets is the pass split on tile-based GPUs, which needs the device. Caching the mesh while the path, range, matrix and colour are unchanged is the next CPU step.
- Not covered: gradient skins (the Argon body is flat), legacy skins, masking, the GL renderer, a live window, and any device.

### 4. Depth and stencil are allocated for a pass that is off

The front-to-back pass is bypassed by default (`FrameworkDebugConfig.cs:19`), so depth testing is off for the whole frame and nothing in the framework or game sets stencil state beyond the renderer's default. The window surface still requests depth 16 and stencil 8 (`SDL3GraphicsSurface.cs:40`), cleared every frame, and the render scale framebuffer adds a D16 buffer (`GameHost.cs:551`). On mobile, both can be dropped while the bypass is on. Turning the pass on without a depth buffer only loses its benefit, since every depth test then passes.

The bypass should stay on for mobile. With the pass on, the back pass draws content at a nearer depth than its own interior (`DrawNode.cs:117-119`), so every visible opaque surface is shaded twice. In gameplay the dimmed beatmap background is opaque with nothing beneath it, which adds a full-screen layer (about 2.3 million fragments per frame at 1080×2160) for no saving.

### 5. Render scale adds full-screen passes on tile-based GPUs

- `Renderer.BeginFrame` clears the backbuffer (`Renderer.cs:549`) before the scaled framebuffer is bound, which leaves a clear-only backbuffer pass. The final blit binds the backbuffer again without a clear or invalidation, so the driver has to preserve and reload it. When scaled, the backbuffer clear can be skipped and its colour, depth and stencil invalidated (`glInvalidateFramebuffer`) right before the blit, which covers every pixel.
- Android can scale the window surface itself: `SurfaceHolder.SetFixedSize(width × scale, height × scale)` makes the display composer upscale the buffer. This removes the framebuffer, the blit and the extra memory, and works for GL and Vulkan alike. SDL3 has to report the new drawable size; SDL already normalises touch coordinates. Composer scaling uses bilinear filtering, as the blit does.
- On GLES, `GLRenderBuffer.Bind` re-specifies the renderbuffer storage on every bind (`GLRenderBuffer.cs:52-55`), for the scaled buffer and for every `BufferedContainer`, every frame. The comment cites discarded contents, which the invalidation on unbind (`GLRenderBuffer.cs:72`) already covers. Re-specification can force a reallocation on some drivers. Profile the driver time before changing it.

### 6. Vertex uploads overwrite buffers the GPU may still read

Batches restart at buffer 0 every frame (`GLVertexBatch.cs:65`) and upload changed ranges with `glBufferSubData` (`GLVertexBuffer.cs:166`). Tile-based GPUs run fragment work for frame N while the CPU records frame N+1, so the driver either copies the buffer (resource ghosting) or waits for the GPU. On desktop GL, `glBufferSubData` was already 13.6% of draw-thread time (`profiling-bottlenecks-2026-10-26.md`).

Candidates:
- One buffer set per frame in flight (three, rotated by frame index).
- A persistently mapped ring buffer with fences; the Adreno 630 driver exposes `GL_EXT_buffer_storage`.

Rotation loses most of the current changed-range upload, which skips unchanged vertices; only a device profile shows which costs more. The 300-frame vertex buffer free threshold (finding 6 of the bottleneck profile) also recreates VAOs and buffers in steady gameplay.

Uniform data has the same pattern. The GLES masking block takes a `glBufferSubData` for each new run of entries between draws (`GLShaderStorageBufferObject.cs:76`), into a 15 KB block that earlier draws of the same pass still read; each update is a candidate for a driver copy. Global uniforms re-specify their buffer with `glBufferData` on every change (`GLUniformBuffer.cs:54`), which avoids waiting at the cost of an allocation per change.

### 7. Texture updates collide with draws in flight

- Bind-time uploads (`Renderer.cs:1453`) can run after draws earlier in the same pass sampled the texture. Glyphs go into single-channel pages of 8192 square (64 MiB) on the Adreno 630. To keep the earlier draws correct, the driver either ends the pass (the cost in finding 3) or copies the texture.
- Frame-start uploads (`Renderer.cs:555-583`) write textures that the previous frame may still be reading; the driver copies or waits for the GPU, which removes CPU/GPU overlap for that frame.
- Regional mipmap generation on GL copies through framebuffers (`region-mipmap-generation.md`), which also ends the pass when it runs mid-frame. The frame-start drain runs after the backbuffer clear (`Renderer.cs:549`); running it before the clear keeps it outside the main pass.

This happens where new glyphs or textures appear (song select scrolling, chat, CJK titles), not in steady gameplay. Smaller font pages on mobile (finding 11) bound the size of any copy.

### 8. Per-vertex masking trades draw calls for fragment work

Per-vertex masking sets the hardware scissor to the whole viewport and clips in the fragment shader, so partly masked geometry is shaded outside its mask. Desktop measurements counted draws; mobile GPUs are usually limited by fill rate. On GLES the masking data falls back to an 80-entry uniform block, which flushes every 80 masking states. Compare on the device with `PerVertexMasking = false` in Settings > Flags..., then restart the game.

### 9. Thread scheduling is left to defaults

No thread priority is set on Android. The platform's own render threads run at `THREAD_PRIORITY_DISPLAY` or `URGENT_DISPLAY`.

- Set the draw and update threads to `Android.OS.Process.SetThreadPriority(ThreadPriority.UrgentDisplay)` from within each thread.
- API 31+: report each frame's work duration to `PerformanceHintManager` (ADPF) with the frame budget as target, so the governor raises big-core frequency before a frame misses. Not testable on the API 29 device.
- `android:appCategory="game"` (API 26+) lets OEM game modes (MIUI Game Turbo, Samsung Game Booster) apply their performance profiles; API 33 adds the Game Mode API. osu!droid declares it together with `android:isGame="true"`.
- High refresh rate: request the fastest display mode through `WindowManager.LayoutParams.preferredDisplayModeId`, plus `preferMinimalPostProcessing` on API 30+, as osu!droid's opt-in "force max refresh rate" setting does.
- `Window.SetSustainedPerformanceMode(true)` where supported trades peak clocks for stable clocks; a user option at most.

### 10. Single-threaded versus multi-threaded execution

Single-threaded mode runs update and draw back to back on one core: about 1.0 ms per frame on the desktop CPU (CoreCLR), several times that on a phone. Multi-threaded mode overlaps them on two big cores. The default is single-threaded; the device's config is multi-threaded. Measure both with the frame limiter fixed, since finding 1 changes how often each runs.

### 11. Texture memory

Single-channel font atlas pages are 8192 square on the Adreno 630 (64 MiB each, plus mip levels). The first launch per driver also allocates and clears an 8192-square RGBA probe target (256 MiB). A 4096 cap on mobile quarters each page and its mip regeneration cost; CJK text then fills more pages.

### 12. APK contents

The APK carries AOT images of assemblies that Android never loads: `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.D3DCompiler`, `SharpGen.Runtime`, `OpenTabletDriver.*`, `HidSharpCore`, `ppy.ManagedBass.Wasapi`, `SDL2-CS`, `Microsoft.Diagnostics.Runtime`, `Microsoft.Diagnostics.NETCore.Client`, `nunit.framework`, `ppy.Veldrid.MetalBindings`. They cost build time and download size, not frame time. Measurement builds must not include `G0V0AndroidVulkanLayer`.

### 13. Touch input batching (latency)

Android batches touch events to the display frame and resamples them unless the view calls `requestUnbufferedDispatch`. For touch play, unbuffered dispatch delivers each event when it arrives. SDL3's Java layer (`ppy.SDL3-CS` 2026.1002.1, inspected with `javap`) reads only `getX`/`getY` in `SDLSurface.onTouch`: it drops the batched historical samples and the event time, and never calls `requestUnbufferedDispatch`. Events are timestamped when SDL receives them, after Android's batching.

### 14. Vulkan on tile-based GPUs

Vulkan bring-up is separate work. Before Vulkan becomes a default on Android: render pass load and store operations (Veldrid loads unless a clear follows the framebuffer bind, and stores depth in every pass including the final swapchain pass, `VkFramebuffer.cs:103` and `VkPipeline.cs:302`), transient depth attachments with lazily allocated memory, and a pipeline cache. The Adreno 630 Vulkan driver dates from 2020; GLES remains the safe default, possibly with a driver allowlist for Vulkan.

Checked and not a problem: the cached fragment shaders keep reduced precision through cross-compilation (123–182 `RelaxedPrecision` decorations in the texture shaders' SPIR-V on the device), and draw counts per frame (24–81 in gameplay, 68 in song select with per-vertex masking) are modest for GLES drivers.

## Reference: osu!droid

osu!droid ([osudroid/osu-droid](https://github.com/osudroid/osu-droid), Apache-2.0, commit `3fbc5f9` of 2026-10-25) is a native Android osu! client on a modified AndEngine with GLES1 fixed-function rendering. Its designs are referenced here; no code is copied.

| Area | osu!droid | g0v0 |
| --- | --- | --- |
| Frame pacing | `GLSurfaceView` in continuous mode at the default swap interval: one draw per refresh. The update thread runs in lockstep, one update per draw (`Engine.java`, `State.notifyCanDraw` / `waitUntilCanUpdate`). | `Limit2x`: two draws per refresh, about half discarded (finding 1). |
| Hit timing | Each touch is placed on the gameplay clock by its OS event time: gameplay time at the last update plus `(eventTime − lastUpdateUptime) × rate` (`GameScene.java:2684-2687`). Judgement precision does not depend on the update rate. | Input is applied per update frame, so timing precision depends on the update rate. |
| Touch samples | Batched `MotionEvent` history replayed in order with its own timestamps (`MultiTouchController.java:78-100`). | Batched samples and event times are dropped in SDL3's Java layer (finding 13). |
| Slider bodies | Three triangle meshes (border, body, hint) drawn in the main pass. Each slider clears the depth buffer and draws its body with `GL_LESS`, so the self-overlapping path covers each pixel once (`SliderBody.java`, `UITriangleMesh.kt`). Snaking rebuilds only the cut ends from cached segment indices. The game uses no framebuffer objects. | One framebuffer per slider, redrawn while snaking, splitting the main pass (finding 3). |
| Surface | EGL RGBA8888, depth 24, stencil 0; opaque `RGB_888` surface; native resolution, no render scale. | RGBA8888, depth 16, stencil 8, neither used by default (finding 4). |
| Refresh rate | Opt-in "force max refresh rate": `preferredDisplayModeId` set to the highest-refresh mode, `preferMinimalPostProcessing` on API 30+ (`MainActivity.java:171-176, 1109-1120`). | Not requested; the system picks the mode. |
| Textures | One texture per image, sized to the source, bilinear, no mipmaps. Queued uploads run at the start of each draw, before the scene (`Engine.onDrawFrame`). | Shared atlases (8192 font pages) with mipmaps; uploads can run at bind time mid-pass (finding 7). |
| Platform | `android:isGame="true"`, `android:appCategory="game"`, `largeHeap`; default thread priorities; no ADPF. | None of these (finding 9). |
| Audio (BASS) | Update period 5 ms, device period 5 ms, device buffer 10 ms, playback buffer 100 ms, non-stop on, device timeout off. | The same, except the device period, which is left at the default (0 in the device log). |

What transfers:

- **Frame pacing and hit timing go together.** osu!droid can draw and update once per refresh because hits are timed by event time. In g0v0 this needs event times from SDL's Java layer, which does not pass them, and an input path in osu-framework that carries per-event times into gameplay judgement and replays. With both, mobile could run at one frame per refresh without coarser judgement.
- **Mesh slider bodies in the main pass** are proven on low-end Android GPUs. Drivers for tile-based GPUs generally execute a mid-pass depth clear inside the pass, so it does not split it. osu!droid's bodies are flat colour bands; g0v0's skin gradients across the body width would need the gradient evaluated from the distance to the path in the mesh shader (`slider-direct-rendering-research.md`).
- **Manifest flags and the refresh-rate option** are small, independent changes.
- **Not transferable:** GLES1 fixed-function rendering and one texture per image. g0v0's batching, texture slots and atlases address draw-call cost that osu!droid does not.

## Measurement plan

All runs on the phone; no desktop windows.

- Build: Release, arm64 only (`-p:G0V0AndroidRuntimeIdentifiers=android-arm64`), `-p:G0V0AndroidProfileable=true`, without `G0V0AndroidVulkanLayer`, renderer set to OpenGL.
- Workload: a device-side variant of `TestSceneOfflineRenderBenchmark` (autoplay replay through the live renderer, started by an environment variable, writing per-frame CSV to external storage), or autoplay of a fixed beatmap.
- Tools:
  - `dumpsys SurfaceFlinger --latency <layer>`: present timestamps, dropped and late frames.
  - `simpleperf record --app top.g0v0.lazer -g`: CPU hotspots including driver code.
  - `perfetto`: CPU frequency, core placement, thermal state.
  - `dotnet-trace` through `dotnet-dsrouter`: managed sampling comparable to the desktop profiles.
  - Android GPU Inspector or Snapdragon Profiler: GPU time and bandwidth.
- Runtime switches: frame limiter in `framework.ini` or Graphics settings, front-to-back pass in Settings → Debug (not persisted), snaking in the osu! ruleset settings.
- A/B, one variable at a time, three 60-second runs each plus one 10-minute thermal run: `Limit2x` / VSync / 1× limit; single / multi-threaded; render scale 1.0 / 0.8 framebuffer / 0.8 `SetFixedSize`; per-vertex masking on / off; front-to-back pass on / off; snaking sliders on / off; Mono AOT / CoreCLR (arm64); vertex buffer rotation prototype.

## Not verified

- Every finding on a device. Desktop timings do not transfer to Adreno or Mali drivers.
- CoreCLR beyond short gameplay windows: online features, imports from other apps, long sessions and memory pressure.
- 90/120 Hz behaviour (whether the game is granted the high refresh mode); the test device is 60 Hz only.
