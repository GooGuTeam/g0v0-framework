# Single-channel font atlas, texture size probe, shared texture atlas

## Single-channel font atlas on by default

`FrameworkSetting.SingleChannelFontAtlas` now defaults to true. Existing configurations saved the old default, so a one-time migration (`SingleChannelFontAtlasDefaulted`) switches them over. Config migrations now skip settings that a subclass does not define; the WASAPI migration previously threw in such configs, which was the cause of the long-standing `TestSceneSpriteTextScenarios.TestConstructor` failure and two localisation test failures.

### One shader for text, icons and sprites

Text and glyph icons used a separate `TextureAlpha` shader for single-channel atlases. Song select alternates text, icons and boxes, so the shader switches split batches: 195 draws per frame against 72 with RGBA atlases (Direct3D 11, per-vertex masking on; 157 of the 163 shader-switch flushes came from `SpriteText`, `SpriteIcon` and `Sprite` boxes).

The default texture shader (`sh_Texture.fs`) now reads single-channel textures itself, from the global uniform `g_SingleChannelSlots` (one bit per texture slot, see `texture-slots.md`). The renderer updates a slot's bit when it binds a texture to that slot, which only happens for slots no vertex in the current batch samples, so the uniform adds no flushes. `SpriteText` and `SpriteIcon` use the default shader for every store, which also covers icons from nested RGBA stores (`OsuIcon.OsuIconStore`). Boxes keep drawing untextured with whatever texture is bound (`Renderer.CanDrawUntextured`), including a single-channel atlas. `TextureAlpha` is no longer used by the framework.

## Texture size probe

On first launch per device and driver, `Renderer.ProbeTextureSizes` allocates and clears square render targets (RGBA 2048, 4096, 8192, then single-channel sizes RGBA did not cover), so lazily allocating drivers commit the memory. Results are cached by `GraphicsProbeCache` (now generic; `texture-size-*.json` under `cache/graphics-probes`). A probe that crashes leaves a marker and the next launch uses conservative sizes (4096) until the cache is cleared.

- `MaxTextureSize` becomes the reported size if every probed RGBA size passed, otherwise the largest verified RGBA size.
- `Renderer.FontAtlasSize`: single-channel atlases use the largest verified single-channel size up to 8192 (64 MiB); RGBA font atlases stay at 4096.
- The probe runs whenever a probe cache exists; `ProbeBindlessTextures` now only gates the bindless probe.

Results on an RTX 5060 Laptop: Direct3D 11 reports 16384, OpenGL and Vulkan report 32768; RGBA and single-channel 8192 pass on all, so the font atlas is 8192. The second launch reads the cache.

## Shared texture atlas (off by default)

`Renderer.GetSharedTextureAtlas` lets texture stores with the same settings share atlas pages; enable with `SharedTextureAtlases = true`. Measured draws per frame (with per-vertex masking):

| Capture | Separate atlases | Shared |
| --- | ---: | ---: |
| Song select (two runs) | 71 / 70 | 71 |
| Gameplay frames 300 / 900 / 1500 | 56 / 43 / 49 | 59 / 44 / 51 |

The game's, ruleset's and skins' textures together still fill three 1024x1024 pages, so sharing only changes how textures split across pages. It stays off.

## Verification

- `TextureSizeProbeTest` (probe logic with fake allocation: all pass, RGBA failure with single-channel success, small reported size, caching per device, interrupted probe, no cache) and 390 texture, font, sprite, config, localisation and graphics tests pass.
- Visual test browser on Direct3D 11, OpenGL, Vulkan and Deferred Vulkan: probe runs and logs, no errors; song select screenshot shows correct RGBA ruleset icons, glyph icons and CJK text with the single-channel atlas.

## Not verified

- Mobile devices: the probe's transient 256 MiB RGBA 8192 allocation and the 64 MiB font atlas on low-memory phones.
- Performance effect of the single-channel and larger font atlas.
