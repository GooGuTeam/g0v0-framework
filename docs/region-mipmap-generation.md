# Regional mipmap generation

## Problem

Every upload into a texture with automatic mipmaps regenerated mipmaps for the whole texture: `CommandList.GenerateMipmaps` on Veldrid, `glGenerateMipmap` on GL. A single new glyph in a 4096-square font page re-reads the full 16-megatexel level 0 and rewrites levels 1–3. On bandwidth-limited mobile GPUs this is a stall whenever new characters appear, and it is why upstream reduced atlas pages to 1024 (see `font-atlas-capacity-optimisation.md`).

Upstream had a regional prototype (disabled in `b972ed213`, "issues on various platforms, most prominently Android"). It rendered each mip level through a framebuffer attached to that level with a mipmap shader, and copied the whole texture once per level to sample from. It has been removed. This implementation does not render into mip levels.

## Principle

Each texel of level `k` is a 2×2 average of level `k − 1`, so it depends only on a `2^k`-square block of level 0. The renderer samples at most `IRenderer.MAX_MIPMAP_LEVELS` (3), so every needed texel depends on an 8×8-aligned block. For a region aligned to 8 texels in a texture whose dimensions are multiples of 8, mipmaps generated for that region alone equal those generated for the whole texture. The region's level-0 contents are read from the texture itself, so neighbouring atlas regions or padding need not be known on the CPU.

`MipmapRegions.TryPlan` aligns each level-0 upload outwards to 8 texels, merges overlapping or touching regions (a glyph and its padding strips become one region), and falls back to whole-texture generation when:

- the regions cover more than a quarter of the texture, or there are more than 16 (typical during loading);
- the texture dimensions are not multiples of 8;
- any upload lies outside the texture;
- `MipmapRegions.Enabled` is false (internal switch for comparison).

Each texture also requires one whole-texture generation after (re)allocation before regional generation is used, so every level is allocated and initialised. A new atlas page gets that from its white-pixel upload. Uploads to levels other than 0, size changes and empty uploads also force a whole-texture generation. Only RGBA8 and R8 textures, and single-resource Veldrid textures (not video), are eligible.

## Backends

**Veldrid (D3D11, Vulkan, Metal, and the Deferred renderer):** `VeldridTexture.GenerateRegionMipmaps` creates a scratch texture of the region's size with levels 0–3, then `CopyTexture`s the region's level 0 into it, calls `GenerateMipmaps` on it, and `CopyTexture`s levels 1–3 back. The scratch texture is released through the renderer's frame-delayed disposal. Only copies and the backend's own generation are used. `VeldridRenderer` records on the graphics command list (where whole-texture generation already happens), and `DeferredRenderer` on its graphics command list.

**OpenGL / OpenGL ES (`GLRenderer`, used on Android):** requires GL 3.0 or GLES 3.0 (`GL_MAJOR_VERSION >= 3`), for framebuffer attachment of non-zero levels. A pooled 512-square scratch texture per format has one framebuffer attached to each of its levels 1–3, attached once. Per region, the source texture's level 0 is attached to a read framebuffer and `glCopyTexSubImage2D`'d into the scratch's top-left corner, then `glGenerateMipmap` runs on the scratch, and each level is copied back with `glCopyTexSubImage2D` from its framebuffer. Framebuffers are only read from. Pending GL errors are cleared before the sequence. Any GL error after it (including incomplete framebuffers) is logged to the performance log, and the texture falls back to whole-texture generation, which overwrites the levels. Regions larger than 512 also fall back.

`GL_GENERATE_MIPMAP_HINT` is not used in the regional sequence: it does not exist in core profiles and raises `INVALID_ENUM`. (The existing whole-texture path still sets it; its error was never checked.)

## Verification

All on Windows, NVIDIA desktop GPU.

- `dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf`: 0 warnings, 0 errors.
- `MipmapRegionsTest` (9 cases): alignment, glyph-plus-padding merging, separate regions, texture edge, large and unaligned textures, out-of-bounds uploads, region count limit, switch.
- Explicit GPU equivalence, `RegionMipmapVeldridGpuTest` (D3D11) and `RegionMipmapGLGpuTest` (desktop OpenGL 3.2 core via a hidden SDL window): a 256-square texture receives a full random upload, then six unaligned random patches in one batch (including a corner, a single texel, and adjacent patches). Its levels 0–3 are read back and compared with a texture that received the same final image in one upload. **0 differing texels on every level on both backends**, and the regional path was confirmed used.
- Timing, `TestSmallUploadCostOnLargeTexture`: 4096-square texture, 30 frames each with one 48×48 upload, wall time per frame including GPU completion, three runs:

| Backend | Whole-texture | Regional |
| --- | ---: | ---: |
| D3D11 | 0.28–0.33 ms | 0.13–0.17 ms |
| OpenGL (desktop) | 0.13–0.17 ms | 0.16–0.22 ms |

  On this desktop GPU, whole-texture generation of a 4096 page is already very fast, so the regional path's fixed driver overhead is comparable on GL. The relative benefit is expected to be larger on bandwidth-limited mobile GPUs, where whole-page generation reads and writes tens of megabytes per upload, but this was not measured.
- Existing texture, atlas, font, sprite text, sprite, buffered container, renderer and masking suites pass, except `TestSceneSpriteTextScenarios.TestConstructor`, which fails in `FrameworkConfigManager`'s constructor independently of this change. Previous explicit GPU tests pass.

- Smoke run of the osu! visual test browser (`TestSceneSongSelect`, about 15 s after entering song select) with `Deferred_Direct3D11`, `Deferred_Vulkan`, `OpenGL` and `Direct3D11`: each reached song select with no exceptions and no regional mipmap failure in the logs. This confirms the paths run without errors, not that their output is pixel-identical on those renderers, and it does not count how often the regional path was taken there.

## Not verified

- Android (OpenGL ES) and iOS (Metal) devices; no mobile build was possible in this environment.
- Pixel comparison on Vulkan, Metal or the Deferred renderer (the offline harness is D3D11 only).
- Mobile frame-time or hitch reduction.
