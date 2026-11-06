# Per-vertex masking

## Problem

Masking parameters (masking rectangle, transform, corner radius, border, blend range) were global uniforms, and clipping to ancestors used the hardware scissor. Every masked drawable with its own masking info therefore ended a batch: in the draw captures from `bindless-texture-research.md`, masking caused 107–114 of about 175 song select draws and roughly half of gameplay draws. Upstream osu-framework moved masking into a storage buffer in 2023 (ppy/osu-framework#5952) and reverted it (ppy/osu-framework#5990) without giving a reason.

## Design

### Masking buffer

`Renderer` appends a `MaskingData` entry (std140, 192 bytes; `g0v0_MaskingData` in `Internal/sh_GlobalUniforms.h`) whenever the applied masking or scissor state changes. Entries are only appended during a frame and reset in `BeginFrame`, so geometry keeps referring to the state it was submitted with. The entry includes the scissor rectangle in vertex (screen) space, the inverse of the mapping used for masking AABBs.

- The buffer is a shader storage buffer (4096 entries) where supported and a uniform block of 80 entries (15 360 bytes, under the 16 KiB GLES 3.0 guarantee) otherwise, which includes Android. When a buffer fills, the renderer flushes and moves to the next one.
- The block is declared in `sh_GlobalUniforms.h`, so every stage of every shader has it. Resource set numbering then stays consistent between stages, whichever stages use it. Upstream's version declared it only in masking shaders, which forced a separate non-masking vertex shader.
- The buffer is bound at draw time without a flush. `VeldridShaderStorageBufferObject` and `DeferredShaderStorageBufferObject` now cache one resource set per layout; they previously created a resource set on every draw.

### Vertices

`TexturedVertex2D` gains `MaskingIndex` (vertex attribute location 6): one more than the masking buffer entry, or 0 for "use `g_MaskingIndex`", a new global uniform holding the applied entry. Vertex batches (Veldrid, GL, deferred) set it when a vertex or quad is added, after the pending masking state is applied. Vertices that never pass through a batch, such as static quad meshes, keep 0 and use the global index, which is correct because those draws still flush on masking changes.

### Shader opt-in

A program uses per-vertex masking when its vertex shader declares the `m_MaskingIndex` input (`sh_Texture2D.vs` does) and its fragment shader calls `getRoundedColor()`. `PerVertexMasking` detects this and adds `#define G0V0_PER_VERTEX_MASKING` to both stages. Then:

- `sh_Texture2D.vs` reads the transform from the masking buffer and outputs the entry index (location 5) and the vertex position for scissor tests (location 6).
- `sh_Masking.h` maps the `g_*` masking names to the entry's fields, and `getRoundedColor()` returns transparent black for fragments outside the entry's scissor rectangle (pixel centres in `[left, right) × [top, bottom)`, as with a hardware scissor).

Other programs are unchanged: custom vertex shaders such as `sh_CursorTrail.vs` and `sh_Texture3D.vs`, and fragment shaders that include `sh_Masking.h` without calling `getRoundedColor()` (such as `sh_RankedPlayBackground.fs`, which writes an opaque colour and relies on the hardware scissor). Fragment shaders that read `g_*` masking values before calling `getRoundedColor()` would read the wrong entry; none in the framework or game resources do.

The fragment shader keeps only the entry index, not a copy of the entry. A copy makes SPIR-V cross-compilation emit a second struct type for the block (its bools become uints), and desktop GL then fails to link with a struct type mismatch between stages.

### Hardware and shader clipping

`ApplyPendingMaskingState` decides per submission whether the geometry can be clipped in the shader. That requires a `TexturedVertex2D` batch, an opted-in shader, and no depth or stencil writes, since a fragment shader that outputs transparent black still writes depth and stencil. In that case the hardware scissor is set to the whole viewport; otherwise it is the exact scissor as before.

- A flush happens when the hardware scissor changes, or when masking or scissor state changes while the pending geometry relies on global uniforms.
- Shader, batch, depth, stencil and scissor-enable changes mark the clipping state as pending, so the mode is re-evaluated.
- Masking interior retention (`masking-interior-retention.md`) now only applies to geometry that is not clipped in shaders.

Set `PerVertexMasking = false` to disable the opt-in for comparison.

## Results

Draw counts per frame, from `FrameDrawCapture` (song select: visual test browser, `TestSceneSongSelect`; gameplay: offline render benchmark, frames 300/900/1500, Argon skin):

| Capture | Before | After | Masking flushes before → after |
| --- | ---: | ---: | ---: |
| Song select, Direct3D 11 (two runs) | 174 / 174 | 72 / 72 | 108 → 0 |
| Song select, Deferred Direct3D 11 | 163 | 59 | 108 → 0 |
| Song select, Deferred Vulkan | 163 | 58 | 108 → 0 |
| Song select, OpenGL | 166 | 68 | 108 → 0 |
| Song select, Vulkan | 174 | 72 | 108 → 0 |
| Gameplay, frame 300 | 110 | 56 | 59 → 0 |
| Gameplay, frame 900 | 84 | 43 | 43 → 0 |
| Gameplay, frame 1500 | 99 | 49 | 52 → 0 |

Remaining draws are mostly batch switches (`SetActiveBatch`), texture changes and blend changes. Vertex counts are unchanged.

Offline render benchmark (D3D11, RTX 5060 Laptop, 1800 frames, three alternating runs), frame times after the first second:

| Case | Before mean / p50 / p99 | After mean / p50 / p99 |
| --- | --- | --- |
| 2400×1080, warm process | 1.41–1.67 / 1.28–1.51 / 3.33–3.94 ms | 1.15–1.46 / 1.05–1.27 / 2.46–3.88 ms |
| 1920×1080, cold process | 2.51–3.23 / 1.80–2.81 / 8.10–10.41 ms | 2.47–3.00 / 1.82–3.00 / 8.93–9.85 ms |

The warm case improves by about 15% in mean and p50. The cold case is dominated by JIT and first-use costs and its run-to-run variance hides any difference. The offline device waits for the GPU every frame, so these numbers understate CPU-side submission savings and say nothing about mobile drivers.

## Verification

- `dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf`: 0 warnings, 0 errors.
- Explicit `PerVertexMaskingGpuTest` renders the same scene with the opt-in disabled and enabled and compares pixels. The scene has rotated nested bordered masks, a mask wider than its parent's scissor, a hollow edge effect (blend range 6, alpha exponent 2, corner exponent 2.5, edge offset), a depth-writing draw in the middle, disabled scissor, and an offset frame buffer set up as `BufferedDrawNode` does. Masking and scissor flushes drop from 9 to 2 (the remaining ones are the depth-writing fallback).
  - Direct3D 11, storage buffer: one pixel differs by one step (an anti-aliased hollow edge; the paths compile to different HLSL).
  - Direct3D 11, uniform-block fallback (`OSU_GRAPHICS_NO_SSBO=1`): identical.
  - OpenGL, storage buffer and uniform-block fallback: identical.
  The test allows a difference of one step per channel.
- The ESSL (GLES) cross-compilation of `Texture2D/Texture` with the uniform-block fallback declares the masking block with the same member precisions in both stages, as the existing global uniform block does.
- Existing explicit GPU tests pass: masking retention (now run with the opt-in disabled, since retention only applies there), masking batching, untextured white pixel, regional mipmaps (Veldrid and GL), GL frame buffer formats, GL program binary cache. 316 graphics, shader, sprite, masking and buffered-container tests pass; the one failure is the pre-existing `TestSceneSpriteTextScenarios.TestConstructor`.
- The visual test browser reaches song select without errors on Direct3D 11, Deferred Direct3D 11, Deferred Vulkan, OpenGL and Vulkan, and a screenshot of song select with the opt-in enabled shows correct rounded corners and clipping.

## Not verified

- Android and iOS devices. Upstream reported marginal gains on Android for its version; GLES drivers may handle the per-fragment uniform-block indexing and buffer updates differently. Measure before relying on it.
- Pixel comparisons on Vulkan, Metal and the deferred renderer (only smoke-tested, without errors).
- Shaders in other projects that pair `sh_Texture2D.vs` with a fragment shader calling `getRoundedColor()` but writing other colours outside it.
