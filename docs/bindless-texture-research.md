# Bindless textures: research (2026-10-26)

Implementation update: Veldrid now supports fixed sampled-image descriptor arrays and optional logical-device descriptor indexing. Framework batching still uses scalar bindings. See [current implementation and remaining integration](atlas-pages-and-descriptor-arrays.md); the measurements and recommendations below describe the earlier capture set.

## Question

Would bindless textures (or an equivalent that lets one draw sample several textures) reduce draw calls enough to be worth implementing, and can the framework's backends support it?

## Method

Per-frame submission captures (`FrameDrawCapture`, `G0V0_DRAW_CAPTURE`) record every `DrawVertices` call with its shader, bound texture, batch and the `FlushBatchSource` that ended the previous batch. Captures were taken from:

- **Song select**, live game (D3D11), three captures from the masking retention and untextured box work.
- **Gameplay**, the offline render benchmark (`TestSceneOfflineRenderBenchmark`, autoplay, Argon skin, D3D11), frames 300, 900 and 1500. A new `G0V0_DRAW_CAPTURE_FRAMES` option captures listed frame indices to `<output>.<frame>.json`, since the benchmark is too short to trigger captures by hand. Captures now also record the texture size and the draw nodes on the stack.

A script then replays each capture and merges consecutive draws whose boundary is removable: same shader, batch and target, a flush reason in the removable set, and at most N distinct textures in the merged run. This estimates the draw count with N texture slots (bindless behaves like a large N), with per-primitive masking (removing `SetMasking` boundaries) and with both.

## Finding along the way: replay export had no glyph atlas

The first gameplay captures showed 22–27 distinct textures per frame, most of them 46×57 glyphs. `OfflineReplayExportSession` created its font store with `new FontStore(renderer, loader, 100)`, which resolves to the three-argument constructor for nested stores, and that one disables the atlas. Every glyph was its own texture. The live game uses an atlased store.

Fixed by calling the overload that takes a filtering mode, which atlases. Gameplay draws per frame dropped from 106–143 to 84–112, and texture-change flushes from 47–56 to 19–20. Offline frame times did not change measurably (2400×1080 warm mean 1.17–1.29 ms before, 1.07–1.19 ms after; the offline device waits for the GPU every frame, which hides submission cost). The captures below are after the fix.

## Where draws come from

| Capture | Draws | SetMasking | BindTexture | SetBlend | SetActiveBatch | Other | Distinct textures |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Song select | 175–182 | 107–114 | 14–17 | 9–10 | 23–24 | 19–20 | 6 |
| Gameplay, frame 300 | 112 | 59 | 19 | 18 | 1 | 15 | 6 |
| Gameplay, frame 900 | 84 | 43 | 20 | 11 | 1 | 9 | 5 |
| Gameplay, frame 1500 | 99 | 52 | 20 | 18 | 1 | 8 | 6 |

Gameplay textures are the 4096² glyph atlas, three 1024² texture atlases and one or two standalone textures. Most texture-change flushes are between two 1024² atlases (10–11 per frame), then between the glyph atlas and a texture atlas (7). The three texture atlases exist because each `TextureStore` owns its own atlas: the game's store, the ruleset's (`DrawableRulesetDependencies`) and each skin's (`Skin`).

## Estimated draw counts

| Capture | Now | 4 slots | 8 slots | Shared texture atlas | Per-primitive masking | 4 slots + per-primitive masking |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Song select (two captures) | 175 / 179 | 159 / 165 | 159 / 165 | — | 76 / 75 | 53 / 55 |
| Gameplay, frame 300 | 112 | 94 | 94 | 101 | 64 | 38 |
| Gameplay, frame 900 | 84 | 65 | 65 | 72 | 51 | 24 |
| Gameplay, frame 1500 | 99 | 80 | 80 | 87 | 57 | 30 |

- Multiple texture slots alone save 17–23% of gameplay draws and 8–9% at song select. Four slots capture all of it, since no frame uses more than six textures; bindless would add nothing over four slots here.
- Per-primitive masking alone saves 40–57%. Masking state is a uniform, so every masked drawable with its own masking info ends a batch.
- The two compound: with both, gameplay draws drop by about 70%. Once masking no longer splits batches, longer runs cross more texture changes.
- A shared atlas across texture stores (no shader change) recovers about 60% of what texture slots save in gameplay.

These are upper bounds. Merging also needs wrap modes to stop being global uniforms (`setWrapMode` flushes under `BindTexture`), and assumes nothing else in the vertex stream or uniforms differs.

## Backend support

| Backend | True bindless | Multiple bound textures with per-vertex selection |
| --- | --- | --- |
| Veldrid D3D11 | No (feature level 11 has no bindless; SM 5.1 descriptor tables are D3D12) | Yes: several texture+sampler bindings in one resource set |
| Veldrid Vulkan | Needs `VK_EXT_descriptor_indexing`; Veldrid exposes no descriptor arrays or partially bound sets | Yes |
| Veldrid Metal | Needs argument buffers; not exposed by Veldrid | Yes |
| Legacy GL renderer (Android) | `ARB/NV_bindless_texture` is desktop-only; mainstream Adreno and Mali GLES drivers do not expose it | Yes: GLES 3.0 guarantees 16 fragment texture units |

The framework already has observation-only probes (`GLBindlessTextureProbe`, `VeldridBindlessTextureProbe`, behind `ProbeBindlessTextures`): on GL they check `GL_ARB_bindless_texture` and compile and make resident a bindless sampler; on Vulkan they report the descriptor indexing features. `BindlessTextureProbeResult.RendererImplemented` is always false. They can tell which devices could support a descriptor-indexing path, but every backend still binds textures.

`g0v0-veldrid` is the team's fork, so descriptor indexing or argument buffers could be added, but each backend would need its own path, D3D11 and GLES would still need a fallback, and the measured scenes do not use enough textures for it to matter.

Portable shader constraints for a slot approach:

- GLES 3.0 only allows constant indices into sampler arrays (GLES 3.2 allows dynamically uniform ones). A per-vertex slot index is neither, so the shader must branch: `if (slot == 0) … else if (slot == 1) …` over separately declared textures. Divergence is limited to fragments where one quad meets another.
- `sh_TextureWrapping.h` and every shader that samples `m_Texture` would need the slot selection. Shaders bind texture and sampler pairs (`sampler2D(m_Texture, m_Sampler)`), so each slot needs its own pair or a shared sampler.
- `TexturedVertex2D` would need a slot index. It is currently 15 floats (60 bytes); one more is about 7% more vertex data.

## Recommendation

1. **Not bindless.** No backend that matters (D3D11, GLES on Android) supports it, and four slots already cover every texture in the measured frames.
2. **Per-primitive masking first** (implemented: `per-vertex-masking.md`). It removes two to three times as many draws as texture slots, and it is the prerequisite for texture slots to pay off fully. It needs the masking parameters per vertex or in a storage buffer indexed per vertex, which `ShaderStorageBufferObjectStack` already does for some state.
3. **Shared texture atlas** as a cheap intermediate: let texture stores with the same filtering and mipmap settings share one atlas. This saves about 10–12 gameplay draws per frame with no shader or vertex changes.
4. **Four texture slots** after per-primitive masking, if draw counts still matter on target devices.

Draw-count reductions do not translate one-to-one into frame time, but flushes are a large share of the draw thread: `profiling-bottlenecks-2026-10-26.md` measured batch flushes at 45% of draw-thread time (masking-triggered ones at 18%), and the D3D11 present path scales with recorded commands. Mobile GL drivers, where per-draw validation is expensive, should benefit most; that should be measured on an Android device before committing to the shader changes.

## Changes made

- `OfflineReplayExportSession`: the export font store uses a glyph atlas.
- `FrameDrawCapture`: `G0V0_DRAW_CAPTURE_FRAMES` captures listed frames; each draw records texture size and the draw nodes on the stack.

## Not verified

- Android devices and GLES driver behaviour; draw-call cost on mobile.
- Gameplay with legacy skins, storyboards or many beatmap textures, which may use more distinct textures per frame.
- The estimates are from submission traces, not an implementation.
