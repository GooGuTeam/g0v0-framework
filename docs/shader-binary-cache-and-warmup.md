# Shader binary cache and warm-up

## Problem

`ShaderCompilationStore` persisted only GLSL → SPIR-V → cross-compiled source. Backend compilation ran on the draw thread on every launch: Veldrid's D3D11 backend called `D3DCompile` on HLSL text in `Factory.CreateShader`, and the legacy GL renderer (Android) compiled and linked GLSL. Shader GPU initialisation was an expensive operation processed **one per frame**, and any shader bound before its turn initialised inline, mid-frame (see `profiling-hitches-2026-10-26.md`).

## Changes

### Direct3D 11 bytecode cache

`VeldridShader.compile()` (which runs in the shader constructor, off the draw thread when loaded asynchronously) now compiles HLSL to DXBC through `ShaderCompilationStore.CompileHlslBytecode` and hands Veldrid bytecode. Veldrid uses DXBC directly (it checks the `DXBC` magic), so `CreateShader` no longer compiles.

- The call mirrors Veldrid's `D3D11Shader.compileCode`: UTF-8 source bytes, entry point, `OptimizationLevel3`, profile `vs_/ps_5_0` at feature level 11.0+ and `4_0` below (read once per device from `ID3D11Device.FeatureLevel`). The bytecode is therefore what Veldrid would have produced.
- Cached by HLSL hash, entry point and profile, with an MD5 integrity check and a version constant. Corrupt entries recompile.

### OpenGL program binary cache (legacy GL renderer, Android)

`GLShader.CompileInternal` restores programs with `glProgramBinary` when a cached binary exists, otherwise compiles, links (with `GL_PROGRAM_BINARY_RETRIEVABLE_HINT`) and saves `glGetProgramBinary` output.

- Enabled when `GL_NUM_PROGRAM_BINARY_FORMATS > 0` (core in OpenGL ES 3.0 and OpenGL 4.1, or `ARB_get_program_binary`).
- Keys include the vertex/fragment source hashes and a hash of `GL_VENDOR|GL_RENDERER|GL_VERSION`, so driver changes miss the cache. A binary the driver rejects (link status 0) falls back to normal compilation.
- Uniform/texture/uniform-block lookups after linking are unchanged.

### Draining shader initialisation

`Renderer.BeginFrame` now runs pending expensive operations (only shader GPU initialisation uses them) until a 2 ms budget is spent, at least one per frame. With cached binaries each is short, so loading screens finish initialisation before gameplay binds the shaders.

### Gameplay shader warm-up (client)

`OsuGame.LoadComplete` loads the shaders seen during gameplay on a background task: Texture, Blur, Grayscale, CircularProgress, Path, PathPrepass, TriangleBorder, SpinnerGlow, ArgonBarPath, ArgonBarPathBackground and CursorTrail/Texture. The CPU part (cross-compile and DXBC) runs on that task; GPU initialisation is queued for the draw thread. On first launch this also fills the disk caches.

### Offline rendering uses the shader cache

`OfflineRenderContext` accepts a shader cache storage. `OfflineReplayExportSession` forwards one, and `OsuGame` passes the host's `cache/shaders`, so replay exports reuse the live game's compilations instead of compiling every shader for each export.

## Results

Offline render benchmark (`TestSceneOfflineRenderBenchmark`, 600 frames, D3D11, RTX 5060 Laptop), two runs each. "Old" is the previous behaviour (HLSL text to Veldrid, one initialisation per frame), selected with a temporary switch that has since been removed.

| Configuration | First 60 frames, 1920×1080 / 2400×1080 | Worst frame in first second |
| --- | ---: | ---: |
| Old, cold cache | 796–817 / 621–624 ms | 242–251 / 157–159 ms |
| Old, warm cache | 805–815 / 647–670 ms | 447–450 / 205–215 ms |
| New, cold cache | 266–277 / 167–179 ms | 84–87 / 19 ms |
| New, warm cache | 342–355 / 190–209 ms | 128–135 / 36–39 ms |

Session setup (scene load) is unchanged: about 1.8–2.5 s with a cold cache and 0.5–1.0 s warm, in both versions. Steady-state p99 is unchanged (about 10 ms); remaining first-frame cost is JIT and other first-use work.

`TestSceneOsuGame` in the visual test browser (real D3D11 renderer, test storage): gameplay shaders including SpinnerGlow, CursorTrail and ArgonBarPath load at startup, no warm-up errors, and `cache/shaders` gained 20 DXBC entries.

## Verification

- `dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf`: 0 warnings, 0 errors.
- Explicit `GLProgramBinaryCacheTest` (desktop OpenGL, hidden SDL window): a second shader instance restores its program from the cached binary (exactly one restore) and renders the same pixel as the compiled one.
- Explicit D3D11 and GL GPU tests (untextured white pixel, masking retention, masking batching, regional mipmaps on both backends, GL framebuffer formats) pass with shaders created through the new path; 69 related unit tests pass.

## Not verified

- Android devices (GLES drivers vary in program binary support and rejection behaviour), Metal and Vulkan (no backend binary cache added; they benefit from warm-up and draining only), and Veldrid's OpenGL backend.
- The gameplay shader list is derived from logged runs; shaders added later need to be added to the list.
