# Whole-quad submission

## Scope

The default `RendererExtensions.DrawQuad()` path now submits four ordered vertices together through the internal `IQuadBatch<TVertex>` interface. This includes normal sprite/glyph drawing and framebuffer composition that use `DrawQuad()` without an explicit vertex callback.

The public `IVertexBatch<in TVertex>` interface, vertex layout, shaders, textures, index order, draw order and existing GPU synchronisation mechanisms are unchanged. Cursor-trail optimisations and Deferred upload-event coalescing are separate work and are not included.

## Submission path

`osu.Framework/Graphics/Rendering/RendererExtensions.cs` prepares four `TexturedVertex2D` values in stack storage, in bottom-left, bottom-right, top-right, top-left order. Shared texture bounds, blend range and draw depth are populated once. Positions, texture coordinates and corner colours retain the original values.

`IQuadBatch<TVertex>.TryAddQuad()` consumes the input synchronously. No backend retains the stack span. An explicit `vertexAction` still receives four individual calls with each vertex constructed immediately before its callback, preserving possible callback state changes. Batches that cannot accept the fast path return false without modifying batch state and use individual submission. Partial primitives are not silently padded, reordered or flushed early to make them fit.

`DrawTriangle()` and clipped-triangle submission remain unchanged.

## Backend handling

### OpenGL

`GLQuadBatch<T>` opts into the internal interface. The batch selects its active state, checks capacity and obtains CPU vertex memory once for the quad. `GLVertexBuffer.SetVertices()` still compares each vertex and extends the same first/last changed range that four `SetVertex()` calls would produce.

Capacity checks use the actual vertex buffer capacity; the GL batch's nominal `Size` remains in quads. Buffer overflow draws and buffer rotation retain the existing order. Initial GL allocation still uploads the CPU buffer through the existing `Initialise()` path, including zero-valued vertices.

### Ordinary Veldrid

`VeldridQuadBatch<T>` likewise selects and obtains a buffer once. Bulk writes retain per-vertex equality checks, new-vertex detection and exact dirty-range tracking. Overflow still advances to a new buffer rather than wrapping. Frame-index buffer rotation and staging copy commands are unchanged.

`VeldridVertexBuffer.Free()` now resets `lastWrittenVertexIndex` as well as releasing storage. When an idle buffer is recreated, a first all-zero write must not be mistaken for already initialised GPU data. This also protects the existing single-vertex path.

The internal `IVeldridVertexBuffer<T>` is now invariant to permit a `ReadOnlySpan<T>` method; no public interface variance was changed.

### Metal

The specialised Veldrid Metal buffer initialises its mapped storage once and copies all four vertices into the shared span. It leaves the explicit synchronisation range untouched, preserving the existing shared-memory path rather than requesting a staging upload.

### Deferred

A complete quad records one existing `AddPrimitiveToBatchEvent` directly, avoiding four temporary assembly steps. The event synchronously copies the span into frame-owned memory. There is still one upload event per quad, and the existing upload pass, render-event pass, statistics and draw cursors are retained.

Single-vertex primitive assembly is now batch-owned rather than static per vertex type. Independent batches/renderers no longer share that mutable temporary array and counter. Partial primitives continue through individual submission.

### Dummy

The dummy batch accepts a complete quad without rendering, retaining its no-op behaviour.

## Expected effect and limitations

The default quad path replaces four delegate submissions and repeated batch/memory acquisition with one internal quad call. It continues to submit four GPU vertices and does not itself reduce draw calls, vertex bytes or fragment work. Equality-based upload avoidance remains enabled.

Explicit callbacks, triangle submission and custom batches that do not implement the internal interface retain their individual-vertex paths. No runtime performance improvement or cross-backend visual equivalence has been measured.

## Client build

Built from `../g0v0` against the modified local framework and existing source dependencies:

```text
dotnet build osu.Desktop/osu.Desktop.csproj -c Debug -warnaserror -p:G0V0SourceBuild=true
```

Result: **0 warnings, 0 errors**, 30.74 seconds.


No tests, application launches, shader probes, benchmarks or additional analysis tools were run. Earlier static slider meshes, replay-seeking synchronisation, text caching and renderer optimisations remain in place. No project-reference files were modified for this patch.
