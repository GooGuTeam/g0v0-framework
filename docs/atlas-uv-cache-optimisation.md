# Atlas full-region UV caching

## Scope

Direct allocations from `TextureAtlas.Add()` now retain their full-region UV rectangle in a private sealed `AtlasTextureRegion : TextureRegion`. Font stores commonly share one such texture per glyph across multiple text draw nodes, so the cache belongs to the texture allocation rather than to each text instance.

Changed implementation:

- `osu.Framework/Graphics/Textures/TextureAtlas.cs`: keep the private backing field typed as `BackingAtlasTexture`, and return the specialised region from `Add()`.
- `osu.Framework/Graphics/Textures/TextureAtlas.AtlasTextureRegion.cs`: immutable mapping and guarded lookup.

The public return type remains `Texture?`, and returned objects remain compatible with `TextureRegion`. Their concrete runtime type is now the private subclass. This can also benefit directly atlas-backed sprites; it does not apply to every texture or every font-store configuration.

## Mapping and cache conditions

The region constructor snapshots the known backing texture's width, height and `ScaleAdjust`, and computes the mapping with the existing arithmetic order:

1. Convert fixed pixel bounds into the parent's display space by dividing by its scale adjustment.
2. Multiply each component by the matching `scale / dimension` normalisation factor.

It does not replace this with a single simplified division, avoiding that algebraic change's potential floating-point rounding differences. The private backing type uses the standard `Texture.GetTextureRect()` mapping; arbitrary custom texture overrides are not assumed to behave the same way.

`GetTextureRect()` returns the cached rectangle only when no explicit area is supplied and all three parent values still match the constructor snapshot. Otherwise it calls the original `TextureRegion.GetTextureRect()` implementation. If backing dimensions later return to their original values, the original mapping is valid again.

The region's own `ScaleAdjust` does not affect a full-region lookup: the original method starts from fixed pixel bounds in that case. Explicit areas do depend on it and always retain the dynamic path. Width/height setters inherited through texture wrappers may change native backing dimensions; checking the actual parent dimensions prevents reuse of the initial mapping in that case.

All cache fields are readonly, with no lazy writes, per-call allocation, shared mutable dictionary or new lock. Lookup does not introduce a mutable cross-thread cache. As before, externally changing texture dimensions concurrently with lookup is not made into an atomic multi-property operation by this change.

## Preserved behaviour

- Atlas reset creates a new backing texture and new allocation objects; existing regions keep their old backing and cache.
- Cropping creates ordinary `TextureRegion` objects. Nested regions pass an explicit area into the parent, so they retain the original coordinate chain.
- Standalone textures and user-defined texture mappings are unchanged.
- `TextureWhitePixel` and its special small-coordinate mapping are unchanged. White-pixel initialisation still uses its existing region path.
- `SetData()` and padding still use the inherited region and backing implementation. Texture availability checks, per-glyph upload checks and the earlier upload-queue optimisations remain active.
- Clamp bounds, inflation, blend range and vertex preparation remain in the existing renderer helpers. There is no cross-frame cache of renderer wrap modes, depth or antialiasing state.
- No glyph ordering, layout, shaders, filtering, mipmaps, resource lifetime or GPU synchronisation changes.

## Expected effect and limitations

A repeated full lookup on an unchanged atlas allocation replaces the region-to-parent conversion and normalisation with dimension/scale comparisons and a cached rectangle return. Each allocation retains an extra backing reference, two dimensions, a scale value and one rectangle. The extra storage is per allocation, shared by users of that texture; no extra GPU resource is created.

This reduces CPU coordinate preparation only. Vertex counts, draw calls, fragment sampling and texture upload bandwidth are unchanged. Explicit areas, nested crops, standalone textures and changed backing dimensions retain dynamic computation. No runtime visual equivalence, cross-backend correctness or performance improvement has been measured.

## Client build

Built from `../g0v0` against the existing local source dependencies:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, 35.41 seconds.


Only source inspection and client compilation were performed. Existing texture-region and atlas tests were read as source references, not modified or executed. No tests, application launches, shader probes, benchmarks or additional analysis tools were run. Earlier slider, text and upload optimisations remain in place. No client implementation or project-reference files were changed for this batch.
