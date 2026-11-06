# Moving-text geometry optimisation

## Scope

This batch changes only `osu.Framework/Graphics/Sprites/SpriteText.DrawNode.cs`. It extends the existing [text submission optimisation](text-submission-optimisation.md) for moving text, including scrolling song-selection panels. Earlier renderer, atlas, texture-upload, carousel and static-slider changes remain in place.

Font loading, glyph lookup, layout, fallback characters, kerning, bitmap resolution, shaders and GPU vertex formats are unchanged. There is no new public API or GPU resource.

## Separate the linear transform from translation

Previously, the per-DrawNode geometry cache required the complete screen matrix to match. Moving otherwise unchanged text therefore rebuilt every inflated glyph rectangle, recalculated inflation percentages and transformed all four corners again.

Each character part now additionally stores a `LinearQuad`: the inflated local rectangle after the matrix's scale, rotation and shear terms, before its final translation. The original 2D transform is evaluated as:

```text
linear.x = matrix.Row0.X * local.x + matrix.Row1.X * local.y
linear.y = matrix.Row0.Y * local.x + matrix.Row1.Y * local.y
screen   = linear + matrix.Row2.Xy
```

The multiplication and addition order follows `Vector2Extensions.Transform`. No transformed corner is reconstructed by adding a width/height vector to another corner.

Reuse requires all of the following to match exactly:

- The resolved glyph-layout version.
- Both inverse-matrix inflation components.
- All four matrix components used by the 2D linear transform.

The lazy layout is still resolved before reading its version and the source matrix, because layout can change the drawable's size. First use and any layout, inflation or linear-transform change rebuild the cached quads. The existing full-matrix equality fast path remains for unchanged geometry and colour-only updates.

For translation-only changes, each screen-space corner is formed from the cached linear corner plus the **current absolute translation**. The algorithm never adds movement deltas to an old screen-space quad, avoiding accumulated drift. It still visits every glyph and refreshes its texture reference from the current layout, retaining the previous transform-change behaviour for custom glyph implementations.

Foreground pixel areas are recalculated from final screen coordinates: floating-point rounding can change area statistics even when the mathematical area is translation-invariant. Shadows are subsequently rebuilt using the existing offset calculation and their areas are calculated independently. Shadow offsets are not assumed translation-invariant because their existing screen-space calculation also involves floating-point subtraction.

All caches remain private to each DrawNode. No mutable geometry is shared between update and draw nodes. Each retained character requires one extra `Quad` (eight floats, 32 bytes), potentially present in each of the drawable's multiple DrawNodes. No full vertex arrays or GPU buffers are cached by this change.

## Shadow colour invalidation

The final shadow colour previously moved out of `Draw()` into `ApplyState()`, but moving text could still recalculate it every applied state. It now retains the applied foreground `ColourInfo` and source shadow colour. The quadratic alpha calculation and existing colour-space composition run when:

- Shadow rendering becomes enabled.
- The effective foreground colour changes, including fades or inherited gradients.
- The shadow colour changes.

Position-only changes with unchanged effective colours reuse the result. The formula, per-corner composition and shadow/foreground ordering are unchanged.

## Unchanged submission and synchronisation

The draw loop is unchanged by this batch:

- Every glyph retains its normal availability and texture bind/upload checks.
- Raw atlas mapping still uses its dimension/scale guards; wrap and inflation-dependent sampling remain prepared dynamically.
- Current renderer depth and vertex colours are initialised on each draw.
- Each glyph's shadow precedes its foreground.
- Unsupported or partial batches retain ordinary quad submission.
- Failed shadow binds retain the foreground retry.
- Deferred frame-owned copying, ordinary backend dirty ranges, buffer rotation and delayed disposal are unchanged.

Whitespace, transparent glyphs and masked glyphs are not newly culled. Legacy outlined text retains all eight stroke copies plus the foreground and its shadow; it can use the moving-text geometry path independently for each copy, but those copies have not been merged.

## Expected effect and limits

For eligible moving text, the change avoids repeated rectangle inflation, inflation-percentage division and the linear portion of four corner transforms. It also avoids clearing/repopulating the character-part list for translation-only changes and avoids repeated shadow-colour composition when its inputs are unchanged.

Final positions, area statistics and shadow positions still update. GPU vertex counts, texture samples, upload checks, submitted quads and drawing order do not change. Text that changes layout or scale continuously still takes the full geometry path and incurs the extra retained-quad storage. There is no measured performance result.

## Validation policy

Only source inspection and desktop-client compilation are authorised for this work. Existing SpriteText scenario/performance and vector utility test sources were read as references; none were added, modified or executed. No application launch, smoke test, shader probe, benchmark or additional static-analysis tool was run.

Compilation does not establish visual equivalence, floating-point identity on every runtime/backend or an actual frame-rate improvement. Runtime confirmation remains outstanding for scrolling, rotation/shear, negative scale, resize, changing text, shadows, inherited gradients, masking and offline export.

## Client build

Built from `../g0v0` against the existing local source dependency graph:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Both builds completed with **0 warnings and 0 errors**: the initial build took **35.27 seconds**, and the final incremental build after retaining transform-time texture-reference refresh took **5.66 seconds**.

