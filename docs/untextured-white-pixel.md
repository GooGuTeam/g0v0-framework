# Untextured white pixel (Box) drawing

## Problem

`Box` is a `Sprite` whose texture is `renderer.WhitePixel`, a white region reserved in a texture atlas. Drawing it costs:

- **Batch breaks.** `Renderer.BindTexture` can reuse the white pixel only while an RGBA atlas is bound. After a non-atlas texture (beatmap background, framebuffer, large texture) the white pixel binds its atlas, flushing, and the next non-atlas texture flushes again. A differing wrap mode also flushes.
- **Fragment work.** Every box fragment runs `wrap()` and `wrappedSampler()` to fetch the constant 1.0. Large boxes (dim layers, panel fills) are pure fill rate, which matters most on mobile GPUs.

## Change

When `Renderer.CanDrawUntextured(texture)` holds, `DrawQuad` and `DrawTriangle` (and therefore `DrawClipped`, used by opaque interiors under masking) do not bind the texture:

- the texture is the white pixel, with `WrapMode.None` on both axes;
- the bound shader is the framework's `Texture2D/Texture` (instances registered in `IRenderer.CreateShader`);
- a texture was bound to unit 0 since the last framebuffer change, so the currently attached texture is valid and is never the render target's own attachment (`textureBoundForCurrentFrameBuffer`, cleared in `setFrameBuffer` and at frame end);
- if a `blendRangeOverride` is given, there is no inflation and no custom texture coordinates, so the drawing-rect blend cannot apply.

The white pixel's texture space is mapped affinely onto `Renderer.UNTEXTURED_RECT` = (-2, -2)–(-1, -1). Texture coordinates, the rectangle and inflation-derived blend ranges are mapped together, so `distanceFromDrawingRect()` sees the same ratios (with better precision than the white pixel's tiny normalised rectangle). `sh_Texture.fs` detects `v_TexRect.x < -1.5`, skips wrapping and sampling, and calls `getRoundedColor(vec4(1.0), v_TexCoord)`. With `WrapMode.None`, the previous path also passed the unwrapped coordinate, and the sampled texel was exactly white. The branch is constant per primitive.

Vertex shaders and layouts are unchanged. Custom shaders never receive marker rectangles because the path requires the default shader. On Veldrid, unit 0 always holds an ordinary texture/sampler pair (video planes attach to consecutive units), so leaving any bound texture attached is valid.

`Renderer.UntexturedWhitePixel` (internal, default true) disables the path for comparison.

## Verification

- `dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf`: 0 warnings, 0 errors.
- `UntexturedWhitePixelTest` (8 cases): marker coordinates and edge-smoothing mapping, no texture bound, framebuffer change (and re-enabling after a bind inside it), other shader, other texture, blend range override, triangles.
- Explicit `UntexturedWhitePixelGpuTest` (D3D11): one frame alternating a non-atlas framebuffer texture with boxes, covering a plain box, a translucent gradient box, a rotated box with edge smoothing, a box in a rounded bordered mask, the clipped triangle path, a hollow glow edge effect with blend range override, and additive and mixture blending. Rendered with the path disabled and enabled: **0 differing pixels**, texture-switch flushes **4 → 0**.
- Existing masking GPU tests and related renderer, masking, buffered container, sprite, sprite text, texture and front-to-back suites pass, except `TestSceneSpriteTextScenarios.TestConstructor`, which fails in `FrameworkConfigManager`'s constructor independently of this change.

## Song select

`osu.Game.Tests` visual test browser, `TestSceneSongSelect` after `SoloSongSelect` was entered, D3D11, one `G0V0_DRAW_CAPTURE` frame per run, two pairs from the same build with a temporary environment switch (since removed). Masking interior retention was enabled in all runs.

| | Draws | BindTexture flushes | SetMasking flushes |
| --- | ---: | ---: | ---: |
| Untextured off | 175 / 175 | 17 / 17 | 107 / 107 |
| Untextured on | 179 / 174 | 14 / 14 | 112 / 108 |

Texture-switch flushes drop by 3 consistently, but total draws do not change beyond frame-to-frame variation (vertex counts differ between captures because the scene animates). The affected boxes in song select also change masking state, so the same boundary is now attributed to `SetMasking`. The draw-call benefit therefore applies where boxes alternate with non-atlas textures under shared clipping. The fragment-side saving (no wrapping or texture fetch for box pixels) applies to every eligible box, but GPU time was not measured.

No GL, Vulkan, Metal or Deferred pixel comparison or frame-time/GPU-time measurement was performed.
