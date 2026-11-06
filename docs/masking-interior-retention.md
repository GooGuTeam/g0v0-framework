# Masking interior retention

## Problem

After `masking-batching.md`, push/pop transitions without geometry no longer split batches, but every masked container that draws something still applies its own masking uniforms and scissor, flushing the batch. In song select, text and icons inside a panel's content container are drawn under a different mask than the panel background before them, so each container boundary costs a draw call even when the geometry never reaches a rounded corner, border or edge.

## Change

`Renderer.TryRetainAppliedMasking()` runs before a complete quad is submitted through the default quad path (`RendererExtensions.DrawQuad` via `TryAddQuadRetainingMasking`, and `SpriteText` glyphs). When clipping state is pending, the quad keeps the **applied** state, joining the current batch, if all of the following hold:

- The bound shader is the framework's `Texture2D/Texture` (registered in `IRenderer.CreateShader`). Custom shaders may read masking uniforms directly and are never eligible.
- The quad's screen-space bounds, mapped as in `setMaskingInfo()`, lie inside both the applied and the pending effective scissor.
- For each of the two states, the quad lies inside that mask's unaffected region (below), or the state is unmasked and the quad has no vertex blend range.
- If the quad has a vertex blend range, both states have the same `AlphaExponent`.

The pending state is left pending. The next quad which does not qualify applies it as before.

### Unaffected region

From `sh_Masking.h`: outside the corner squares, `dist = radius - e`, where `e` is the distance to the nearest straight edge. With `radiusCorrection` as computed in the shader, `alphaFactor` and `colourWeight` both reach 1 when `e >= max(blendRange, borderThickness) - radiusCorrection`. In that region `getRoundedColor()` returns the content colour, attenuated only by the quad's own drawing-rect blend and `pow(·, AlphaExponent)`, which is independent of the mask rectangle, radius, corner exponent and border colour.

The tested region is the union of two masking-space bands that avoid the corner squares: full width with `max(radius, edgeInset)` vertical inset, and full height with the same horizontal inset. `edgeInset` adds a 1 unit margin for CPU/GPU transform rounding. The quad's four corners are transformed with `ToMaskingSpace` including the homogeneous divide used by `sh_Texture2D.vs`; their bounding box must fit in one band. Hollow masks, negative radii, non-positive blend ranges and NaN fail.

## Results

Song select, `osu.Game.Tests` visual test browser, `TestSceneSongSelect` after `SoloSongSelect` was entered, D3D11, one `G0V0_DRAW_CAPTURE` frame per run. Both runs used the same build with a temporary environment switch (since removed):

| | Draws | SetMasking flushes |
| --- | ---: | ---: |
| Retention off | 182 | 114 |
| Retention on | 175 | 107 |

Most remaining masking flushes are single quads under masks they actually touch (panel and pill backgrounds, hollow shadow edges). Those differ in mask parameters and cannot share a draw without per-primitive mask data in the vertex format and shaders. That ABI change remains out of scope here; it is now done for shaders that opt in, see `per-vertex-masking.md`. Retention still applies to geometry that is not clipped in shaders.

## Verification

- `dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf`: 0 warnings, 0 errors.
- `MaskingInteriorRetentionTest` (13 new batching cases: interior, straight-edge band, corner, blend range, border, hollow, scissor, other shader, alpha exponent, re-application) and existing `MaskingBatchingTest`: pass.
- Explicit `MaskingInteriorRetentionGpuTest`: renders nested bordered/rounded masks with interior, edge-band (close to the threshold) and corner geometry, with and without vertex blend ranges, unrotated and rotated 17°. The retained path (default quad submission) is compared byte-for-byte with the per-vertex path, which always applies pending state: identical on D3D11, with fewer masking flushes.
- Existing explicit `MaskingBatchingGpuTest`: pass.
- Related renderer/masking/buffered/sprite text suites: all pass except `TestSceneSpriteTextScenarios.TestConstructor`, which fails in `FrameworkConfigManager`'s constructor independently of this change.

No GL, Vulkan, Metal or Deferred pixel comparison, frame-time measurement or measurement in the full client with real beatmaps was performed.
