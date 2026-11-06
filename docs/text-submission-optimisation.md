# Text submission optimisation

## Scope

This extends the existing per-draw-node screen-space glyph cache and whole-quad batch submission. Later changes reduce [empty upload-queue locking](texture-empty-upload-optimisation.md), [global queue membership scans](texture-upload-queue-optimisation.md), [full atlas UV calculation](atlas-uv-cache-optimisation.md) and [moving-text geometry preparation](text-motion-optimisation.md). The moving-text client output is `Debug-render-text-motion`; the build recorded here covers this earlier submission batch.

This submission batch changes only:

- `osu.Framework/Graphics/Sprites/SpriteText.DrawNode.cs`
- `osu.Framework/Graphics/Rendering/RendererExtensions.cs`

Font loading, glyph rasterisation, text layout, shaders and GPU vertex formats are unchanged. Existing slider and renderer optimisations remain in place. There are no new offscreen textures, static GPU text meshes or public APIs.

## Cached draw-node state

Each screen-space character part now retains its shadow quad and the integer pixel areas of its foreground and shadow quads. Foreground geometry and area are rebuilt with the existing layout-version, transform and inflation cache. Shadow geometry and area are rebuilt when foreground geometry changes, the shadow becomes enabled, or its screen-space offset changes.

The shadow area is calculated from the actual translated quad rather than assumed equal to the foreground area: floating-point coordinate rounding can affect the result. Non-positive areas remain excluded from pixel statistics.

Final shadow colour is calculated during `ApplyState()`, retaining the existing average-colour intensity, quadratic alpha fall-off, child-colour composition and colour-space operations. An unchanged draw node can reuse that result. Colour or shadow changes still invalidate its applied state.

All cached state remains per draw node. This submission batch adds one shadow quad and two `long` area values per glyph; no full per-glyph vertex arrays are retained. The later moving-text change adds a linear-transform quad to reuse geometry during translation and keys shadow-colour composition by its inputs. Layout, scale and inflation changes still require geometry regeneration.

## Glyph submission

Each draw allocates two four-vertex stack spans, one for foreground and one for shadow. Corner colours and the current renderer depth are initialised once per span and reused across glyphs. Depth is read after `base.Draw()` on every draw, not cached across frames or render targets.

Each glyph still uses normal texture availability, upload and wrapping checks. After a successful bind, its shadow and foreground share the same resolved texture coordinates, bounds and blend range. The helper runs for each glyph on each draw; the later atlas optimisation can reuse the raw full-region mapping when its backing dimensions and scale still match. Inflation and wrap-dependent bounds remain dynamic. Texture dimensions, custom mappings and wrap modes are not assumed immutable across frames or across the entire text.

`RendererExtensions.GetQuadTextureCoordinates()` contains the coordinate calculation shared with ordinary `DrawQuad()`. It preserves the pre-clamp sampling rectangle, half-atlas-margin clamp bounds and original inflation arithmetic. The public quad path, explicit callbacks and triangle path retain their existing behaviour.

Submission order remains:

```text
Glyph 1 shadow -> Glyph 1 foreground -> Glyph 2 shadow -> Glyph 2 foreground -> ...
```

Each quad uses bottom-left, bottom-right, top-right, top-left vertex order and the existing batch. The backend consumes the stack span synchronously; Deferred copies it into frame-owned memory before returning. Existing buffer rotation, dirty-range comparison and GPU synchronisation remain in use.

A batch without the internal whole-quad interface uses ordinary `DrawQuad()`. If whole-quad submission declines a partial primitive, that quad likewise uses the existing individual submission path. A failed initial shadow texture bind still allows the original foreground retry. Successful submissions add the cached positive area to pixel statistics exactly once; fallback submissions use ordinary quad statistics.

## Expected effect and limitations

The change removes repeated shadow translations and quad-area calculations from normal draw submission, reuses common colour/depth initialisation across glyphs, and avoids a second texture upload check and coordinate resolution for each successfully bound shadow/foreground pair. It does not skip upload checks for subsequent glyphs or subsequent frames.

The number of GPU vertices, submitted quads and shader samples is unchanged. No reordering, glyph culling, reduced-quality filtering, event coalescing or new batching boundary is introduced. There is no measured frame-time improvement, and compilation alone does not establish visual equivalence or cross-backend correctness.

## Client build

Built from `../g0v0` against the modified local framework and existing source dependencies:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, 40.80 seconds.


No tests, application launches, shader probes, benchmarks or additional analysis tools were run. No client implementation or project-reference files were changed for this patch.
