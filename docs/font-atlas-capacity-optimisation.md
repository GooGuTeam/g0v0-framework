# Font atlas capacity experiment

This document records the capacity-only build. The subsequent [free-rectangle packing batch](font-atlas-packing-optimisation.md) retains this capacity and changes only font allocation placement. `Debug-font-atlas-4096` remains the row-packing comparison build; current source uses free-rectangle font packing.

## Motivation and source evidence

The user identified high-definition ASCII glyphs combined with small texture atlases as one cause of increased draw calls. Source inspection confirms the relevant mechanism:

- `TextureStore` defaults to a maximum atlas side of **1024**, capped by `IRenderer.MaxTextureSize`.
- The root `Game.Fonts` store previously used that default. The nested framework font store shares the root atlas through `FontStore.AddStore()`.
- `GlyphStore` can substitute larger printable-ASCII bitmap uploads from an optional `-ASCII-HD` companion. Original advance, offset, baseline and kerning metrics remain in control; larger texture dimensions still consume more atlas space.
- In the capacity-only build, `TextureAtlas` packs allocations in rows. When the current page fills, it creates a new backing texture. Existing regions retain their previous backing texture.
- `Renderer.BindTexture(INativeTexture, ...)` flushes the current batch when switching native textures. A text sequence alternating between atlas pages can therefore split into multiple batches even with one shader and font.

This confirms how the reported effect can occur, not the exact cause or magnitude of the user's current draw-call count. No runtime trace or measurement was performed. Font size on screen is distinct from bitmap pixel size, and the number of source PNG pages is distinct from the number of runtime atlas pages.

## Implementation

`osu.Framework/IO/Stores/FontStore.cs` now creates its own atlas at:

```text
side = min(4096, renderer.MaxTextureSize)
```

It disables the base `TextureStore` atlas construction and creates the font-specific atlas only when the existing `useAtlas` argument is true. This avoids constructing an unused default atlas. Native page allocation remains lazy, through the existing `TextureAtlas` allocation path.

Scope and preserved behaviour:

- Root game fonts and custom-filter font stores that already own atlases use the larger capacity.
- Nested stores without their own atlas still inherit the parent atlas.
- Ordinary image `TextureStore` instances remain at **1024**.
- The public three-argument `FontStore` constructor remains non-atlased until attached to an atlased parent. This preserves existing ownership and nesting semantics.
- The current standalone font store in `OfflineReplayExportSession` uses that non-atlased constructor and is unchanged by this batch. Enabling atlasing for export is separate work.
- Filtering mode, automatic mipmaps, atlas padding, white-pixel allocation, region UV handling, upload checks and resource lifetime are unchanged.
- There is no repacking or resizing of an existing page, no draw reordering and no cross-renderer sharing.
- This change does not preload ASCII characters or guarantee that a font fits on a single page. Load order, glyph dimensions, other fonts and fallback characters still determine packing.

`osu.Framework/Graphics/Textures/TextureStore.cs` also removes the hard-coded `1024x1024` claim from its oversize-allocation warning, because derived font stores now use a different capacity. The warning still reports the requested dimensions and standalone allocation fallback. Existing `TextureAtlas` initialisation and overflow messages continue to report the actual page dimensions.

No public signatures, client implementation, shader files, font assets or project references were changed.

## Trade-offs

A 4096-square page has **16 times** the base-level pixel capacity of a 1024-square page. This is a capacity ratio, not a promised draw-call reduction or memory saving.

Approximate RGBA8 storage per page, before driver alignment and staging allocations:

| Side | Base level | Including mipmaps |
| --- | ---: | ---: |
| 1024 | 4 MiB | about 5.3 MiB |
| 4096 | 64 MiB | about 85 MiB |

The actual allocated mip chain differs by backend: the current GL path limits texture levels to the framework maximum, while Veldrid allocates a full chain and limits sampling LOD. Total memory depends on the number and occupancy of pages; larger mostly empty pages can increase memory consumption substantially.

Upstream commit `15396029646c734c79904f1df648b2655e9eb40a` reduced the atlas limit from 4096 to 1024 specifically because whole-texture GL mipmap generation was expensive after regional mipmap generation was disabled. That concern still applies. Initial page clearing/upload is larger, and new glyph uploads can regenerate mipmaps across a larger page, especially during cold loading or frequent discovery of new characters.

This experiment deliberately preserves mipmaps and filtering rather than trading away small-text quality. It does not enable the disabled regional mipmap prototype. Fewer texture switches can improve steady-state rendering while increasing loading cost; compilation cannot establish the net result or an optimum size.

## High-definition asset availability

The current workspace contains the `-ASCII-HD` loading implementation and its documentation, but no matching companion files were found under the local application resources tree or framework resources tree. Existing font assets may themselves have different resolutions; no claim about their generation resolution is made here. The user's running resource set may differ from this checkout.

This build changes atlas capacity for the available fonts. It does not supply missing HD companions, regenerate fonts, replace source bitmaps or disable existing HD loading support.

## Client build

Built from `../g0v0` against the existing local dependency graph:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, **16.20 seconds**.


Earlier text geometry/submission, texture scheduling, carousel and static-slider optimisations remain included. Only source inspection and client compilation were performed; existing test sources were read without modifying or executing them. No application launch, smoke test, benchmark, GPU probe or additional static-analysis tool was run.

Runtime confirmation remains outstanding for actual atlas occupancy, draw calls, cold glyph-loading pauses, memory usage, text appearance, device limits and cross-backend behaviour.
