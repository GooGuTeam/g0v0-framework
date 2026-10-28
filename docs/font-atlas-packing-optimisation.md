# Font atlas free-rectangle packing

## Scope

This batch follows the [4096-square font atlas capacity experiment](font-atlas-capacity-optimisation.md). Font-owned atlases now use incremental, unrotated free-rectangle packing. Ordinary image atlases and the renderer's standalone white-pixel atlas retain their existing row allocator.

Implementation files:

- New `osu.Framework/Graphics/Textures/TextureAtlas.FreeRectanglePacker.cs`.
- Modified `osu.Framework/Graphics/Textures/TextureAtlas.cs`.
- Modified `osu.Framework/IO/Stores/FontStore.cs`.

The existing public `TextureAtlas` constructor is unchanged and selects row packing. An internal overload selects free-rectangle packing; only font stores opt into it. No public API, shader, vertex format, glyph metric, font asset or client project reference was changed.

## Why the previous allocator wastes space

The row allocator advances horizontally and starts a new row below the tallest allocation in the previous row. It does not return to fill the gaps below shorter allocations. Mixed glyph heights, fallback fonts and larger ASCII bitmaps can therefore leave unusable space even without considering rotation.

Source PNG page occupancy and runtime atlas occupancy are separate. Runtime glyph uploads are extracted from source pages and packed again. This batch changes that second allocation stage.

## Allocation algorithm

The packer maintains free rectangles for the current backing page. For each padded request:

1. Scan free rectangles which can contain the request without rotation.
2. Prefer the smallest leftover short side, then the smallest leftover long side. Equal candidates retain list order.
3. Place the allocation at the chosen rectangle's top-left corner.
4. Split **every** intersecting free rectangle around the allocated footprint into up to four residual rectangles.
5. Discard fragments which cannot fit even the minimum padded footprint, and prune contained or duplicate rectangles.

Free rectangles may overlap each other, representing alternative placements. None may overlap an allocated footprint. Subtracting a new footprint from every intersecting free rectangle preserves that invariant; subtracting it only from the chosen rectangle would be incorrect.

New fragments are collected separately from traversal of existing rectangles. Surviving old entries were already pruned, so containment comparisons are performed when inserting new fragments rather than repeating all old/old comparisons. Both lists retain capacity across allocations and reset with the backing page. Allocation takes place under the atlas' existing retrieval lock; there is no new drawing-time packing work.

This is an online heuristic, not globally optimal packing. It still depends on arrival order and can produce fragmentation. Placement scans and fragment processing add CPU work and metadata storage at glyph-load time. With many free rectangles, insertion/pruning and list removals can have quadratic worst-case cost. No occupancy or loading-time benchmark has been run.

## Padding and white-pixel reservation

Existing padding is preserved:

```text
PADDING = (1 << MAX_MIPMAP_LEVELS) * Sprite.MAX_EDGE_SMOOTHNESS
        = 8 * 3
        = 24 pixels
```

The allocator reserves a `(glyph width + 24, glyph height + 24)` footprint. The existing backing texture upload pads each glyph by 12 pixels on each side. Disjoint footprints therefore preserve the required separation, including when a later glyph fills a gap above, below or beside an older allocation.

The usable area begins at `(24, 24)`. Footprints include trailing padding through the page edge; the actual glyph still ends at least 24 pixels before that edge, matching the existing acceptance bounds. The origin white pixel reserves its existing position and separation before any glyph allocation. The original empty-page fit checks, including the white-pixel corner restriction, remain unchanged. Zero-sized allocations still reserve padding; negative sizes are rejected by the new private packer.

The 24-pixel separation is not packing waste that can simply be removed. It supports the current filtering, mipmaps and inflated edge sampling. This batch does not reduce it.

## Page and texture lifetime

When no free rectangle on the current page fits an otherwise valid allocation, the atlas creates a new backing page using its existing reset path. Previously returned regions retain their original backing texture and UV coordinates. No existing pixels or regions are moved, resized or repacked.

The packer does not revisit older pages after rollover and does not reclaim disposed regions. It keeps metadata only for the current page, matching the existing rollover model. Cross-page reuse and eviction would require separate lifetime and locality decisions.

The existing immutable full-region UV cache remains in use, including its backing dimension/scale guards and explicit-area fallback. Upload padding, wrap modes, texture availability checks, native uploads, automatic mipmaps, filtering, white-pixel binding reuse and delayed resource disposal are unchanged. Draw order is unchanged; a different distribution of glyphs among backing pages can change texture switches and batch boundaries.

## Rotation remains unimplemented

Rotated placement needs coordinated support for bitmap upload orientation, logical glyph width/height, per-corner UV orientation, cropping and partial uploads, wrap/padding direction, and mip levels. The current `GetTextureRect()` contract returns an axis-aligned rectangle; swapping allocation dimensions alone cannot encode the necessary 90-degree UV mapping.

This batch implements gap reuse without rotation. It makes no claim that lack of rotation is the dominant remaining source of wasted space, or that every text run will fit on one page.

## Separate build outputs

Both builds include earlier retained text, renderer, carousel and slider changes:

| Output | Font page capacity | Font packing |
| --- | --- | --- |
| `Debug-font-atlas-4096` | Up to 4096 square | Existing row allocator |
| `Debug-font-atlas-packing` | Up to 4096 square | Unrotated free rectangles |

Both are capped by `renderer.MaxTextureSize`. Ordinary image atlases remain at up to 1024 square in both builds. Larger font-page memory and whole-page mipmap-generation costs described in the capacity document still apply.

Built the latest desktop client against local dependencies:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, **16.86 seconds**.


Only source inspection and client compilation were performed. Existing atlas tests were read as behavioural references; no tests were added, changed or run. No application launch, smoke test, packing simulation, benchmark, GPU probe or extra static-analysis tool was run. Runtime overlap/padding correctness, visual equivalence, occupancy, memory usage, loading time and draw-call reduction remain unverified.
