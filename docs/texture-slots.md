# Texture slots

The default texture shader (`Texture2D/Texture`) samples one of four texture units per primitive, so draws using different textures can share a batch. This works on every backend that has texture units (D3D11, OpenGL and GLES, Metal); bindless textures are not needed, since measured frames use at most about four textures between other batch breaks.

## How it works

- `TexturedVertex2D.MaskingIndex` packs `(masking entry + 1) * 4 + slot`. `sh_Texture2D.vs` unpacks it and outputs `v_TextureSlot`; the vertex format is unchanged.
- `sh_Texture.fs` declares `m_Texture1..3` (sets 1 to 3) only when the vertex shader outputs `v_TextureSlot` (`G0V0_TEXTURE_SLOTS`, see `TextureSlots.cs`). Other vertex shaders paired with it, such as the game's cursor trail, use set 1 for their own uniforms.
- `Renderer.BindTexture` binds to unit 0 as before unless the default texture shader is bound. Then it reuses a slot already holding the texture, or replaces a slot no vertex in the current batch samples, and flushes only when all four are in use.
- After a draw, the current slot stays marked as used: a batch that draws because it is full continues with the primitive being added, which was already stamped with that slot.
- `g_SingleChannelSlots` has one bit per slot for single-channel (R8) textures.
- Before each draw with the default shader, empty slots get the white pixel's texture, since every declared texture must be bound. A frame buffer change empties all slots, so a frame buffer's own texture is never left bound while drawing into it.
- `TextureSlots = false` binds everything to unit 0, for comparison.

## Vulkan: bound slots disabled

With slots, launching the game on Vulkan (immediate and deferred renderers, RTX 5060 Laptop, driver 591.91) crashed with heap corruption (`0xc0000374` in `ntdll`) shortly after startup in two to four of four launches. The offline Vulkan device rendered the GPU test scenes identically, the crash did not happen with slots disabled, and no frame buffer was found bound to a slot. The cause is not known; `VeldridRenderer` and `DeferredRenderer` disable slot binding on Vulkan (`BindsTextureSlots`). The shader still declares the slots there, filled with the white pixel's texture.

## Vulkan bindless table (experimental)

`VulkanBindlessTextures = true` makes the immediate Vulkan renderer use slots again, through descriptor indexing instead of four bound sets (`VeldridBindlessTextures`, `VeldridTextureTable`):

- The default shader declares `m_Textures[16]` with a linear and a nearest sampler in set 0, indexed with `nonuniformEXT`. `TextureSlotCount` becomes 16, and `G0V0_TEXTURE_SLOT_COUNT` unpacks the slot in `sh_Texture2D.vs`.
- Renderer units 0 to 15 mirror the table, so the slot logic above is unchanged. Before a draw with the default shader the table is attached at unit 0; afterwards unit 0 gets its bound texture back for other shaders.
- Textures share samplers. Each texture's view (`VeldridTexture.GetBindlessView`) covers the mip levels its own sampler would use, and `g_NearestSlots` selects nearest filtering. Reflection does not report array sizes, so `m_Textures` gets its length after compilation.
- A new resource set is created when an entry or view changes, at most once per draw.
- The device requests descriptor indexing when bindless is requested; it is used only when the device enables it (logged as `Bindless textures: table of 16`).
- `OfflineVulkanRenderer = true` runs the offline device on Vulkan, so this is testable without a window.

Results, offline Vulkan (same build, bindless off/on): draws at frames 300 / 900 / 1500 53 → 37, 42 → 24, 48 → 31, texture-change flushes 16–19 → 0, the same as four slots on D3D11. Frame time showed no measurable change (2400x1080 mean 1.021 → 1.002 ms, within run-to-run variation). `TextureSlotsGpuTest` on offline Vulkan with bindless: pixel-identical, texture flushes 335 → 18. The other Veldrid GPU tests pass on it too, except for an existing Vulkan difference below.

In a window (visual test browser, song select, Vulkan with bindless): three launches of 25 s, all reached song select with the table active and none crashed. Four bound sets had crashed in two to four of four launches.

Not verified: gameplay in a window with bindless; other GPUs and drivers.

Found along the way: on offline Vulkan, with or without bindless, the untextured white-pixel path differs from sampling the white pixel in 121 pixels by up to 174 levels (`UntexturedWhitePixelGpuTest`); on D3D11 they match.

## Results (Release, D3D11)

Offline benchmark captures (autoplay, Argon, 2400x1080; identical vertex counts):

| Frame | Before | After | Texture-change flushes |
| --- | ---: | ---: | --- |
| 300 | 53 | 37 | 16 → 0 |
| 900 | 41 | 24 | 18 → 0 |
| 1500 | 48 | 31 | 19 → 0 |

Song select (live, before the Vulkan change): Direct3D 11 72 → 57, OpenGL 69 → 54, texture-change flushes 14 → 0.

Offline benchmark frame time, three alternating runs: 2400x1080 mean 1.140 → 1.090 ms, p95 1.716 → 1.623; 1920x1080 mean 1.298 → 1.245 ms. Run-to-run variation is of the same size.

## Verification

- `TextureSlotsGpuTest` (D3D11 and OpenGL; Vulkan through a temporary offline override): five textures including a single-channel one, boxes, overlapping quads, a frame buffer texture and 300 further textures that fill batches. Pixel-identical to binding unit 0, texture flushes 335 → 107. The 300-texture part found the full-batch bug above.
- `UntexturedWhitePixelGpuTest` also renders with slots; other GPU tests and 295 texture, font, sprite, masking, shader and renderer tests pass.
- On OpenGL, the first render in a new context draws the scene's frame buffer differently with or without slots, so the test renders once before comparing.

## Not verified

- Android GLES devices.
- Why Vulkan crashes; Metal.
- Running several OpenGL GPU tests in one test process crashes the test host (also with the existing per-vertex masking GL test run twice); each class passes on its own.
