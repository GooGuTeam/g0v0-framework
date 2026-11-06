# Draw overhead and ASCII resolution selection

## Implemented font selection

`TextBuilder` and `SpriteText` preloading now pass `FontUsage.Size` to the font lookup. Each concrete `FontStore` selects the optional ASCII-HD companion only when size is strictly greater than its own `ScaleAdjust` (normally 100). Equal or smaller sizes use normal glyphs. This is a logical font-size threshold; drawable transforms and display DPI are not included. `LowResolutionFonts` takes precedence.

The companion descriptor is lazy and probed once per glyph store, only for a qualifying printable ASCII request. Missing or invalid companions fall back to normal glyphs. Normal/HD texture keys and glyph cache entries are distinct. Nested stores cache the owning store and apply that store's threshold on each lookup. Calls without a size use normal resolution. No public lookup interface change is required; custom lookup stores retain their existing behaviour.

Reducing resolution lowers glyph upload volume and atlas occupancy. It does not reduce the number of glyph quads, guarantee fewer atlas pages, or eliminate per-glyph CPU submission work. Loading an HD glyph for the first time can still incur a decode/upload cost.

## Remaining overhead, from current source

- `SpriteText.DrawNode.Draw`: per glyph, checks availability, calls BindTexture, obtains padded UV coordinates, prepares four 60-byte vertices, and submits a quad. Shadows add another quad in the existing interleaved order. Cached geometry already avoids repeat layout/transforms; it does not eliminate submission. A same-page run API could amortise batch/capacity checks, but must preserve pending atlas uploads and foreground/shadow ordering.
- `Renderer.BindTexture`: invokes native Upload before the native texture identity check. Empty queues already have a fast path, and repeated native bindings are already suppressed. Counting BindTexture calls as GPU draw calls would overstate overhead.
- `DeferredVertexBatch.TryAddQuad` and `AddPrimitiveToBatchEvent.Create`: one resource reference, memory-region allocation/copy and event per quad. `EventProcessor` traverses uploads and render events separately. Adjacent compatible primitive runs are the main remaining structural opportunity here, subject to allocator boundaries and vertex writer/draw cursor alignment. The allocator pools 2 MiB blocks; this is not a managed heap allocation per quad.
- `Renderer.FlushCurrentBatch`: texture, shader, blend, masks, projection, framebuffer and batch changes can split a run. Capture the actual reasons before changing atlas sizes or local batching. Transparent draw order must remain intact.
- `BufferedContainerDrawNode.ApplyState`: recomputes Gaussian kernel radius from sigma even when only other drawable state changes. Caching the exact kernel calculation is a smaller CPU opportunity. Existing framebuffer reuse must be accounted for before claiming redundant blur renders.

This is source evidence, not a measured CPU ranking or an FPS improvement claim.

## One-frame trace

Set `G0V0_DRAW_CAPTURE` to an absolute JSON output path before starting the client. After reaching the target screen, create `<output>.request`. The next framework frame records ordered `Renderer.DrawVertices` submissions and writes the JSON once. The request file is retained. Use a fresh output path for another run.

The trace includes vertex count, topology, shader label/identity, texture identity, backbuffer destination, batch type and flush reason. `DirectOrCapacity` includes draws outside an explicit flush. IDs are process-local hashes, not GPU handles. This is framework submission instrumentation, not a GPU command capture, GPU timing measurement, or a mapping to individual drawable names. Deferred backends may further split submissions during playback. Diagnostic collection and JSON writing perturb the captured frame's CPU time; do not benchmark that frame's duration.
