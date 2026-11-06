# Further rendering optimisation research

## Scope and status

Source-only research following `rendering-cpu-optimisation.md`. No renderer implementation, shaders or client code were changed during this research pass. No builds, tests, application launches, probes or benchmarks were run during that pass. All earlier optimisations and the withdrawn slider-gradient experiment's rollback remain unchanged.

Subsequent implementation: the default whole-quad submission path described in section 1 is now implemented and client-built; see `quad-submission-optimisation.md` for its exact scope and limitations. Both the empty local upload-queue path and global queue membership in section 3 are also implemented and client-built; see `texture-empty-upload-optimisation.md` and `texture-upload-queue-optimisation.md`. The other proposals below remain research.

The ranking below considers the amount of structurally redundant work and implementation risk. It is not a measured ranking of current frame-time bottlenecks. Ordinary Veldrid and Deferred must be considered separately: improving Deferred event recording alone will not improve ordinary Veldrid drawing.

## Recommended order

| Order | Candidate | Main opportunity | Important limitation |
| --- | --- | --- | --- |
| 1 | Submit complete quads through an internal primitive path | Broad CPU savings across sprites, glyphs and UI | Does not itself reduce vertex bytes, draw calls or fragment work |
| 2 | Incremental cursor-trail snapshots and cached geometry | Repeated work in an always-present gameplay drawable | Preserve slot order, clock behaviour and per-node isolation |
| 3 | Empty-upload fast path and constant-time queue membership | Per-quad locking and loading-time queue scans | Requires explicit publication and lock-order rules |
| 4 | Coalesce Deferred primitive upload events | Fewer events, resource references and small memory copies | Deferred only; write/draw buffer boundaries must remain consistent |
| 5 | Cache blur kernels and separate content/effect invalidation | CPU kernel work; potentially less GPU arithmetic and redundant rendering | Static backgrounds already cache and downsample; shader precision needs runtime evaluation |

## 1. Whole-quad submission

### Observed path

- `osu.Framework/Graphics/Rendering/RendererExtensions.cs:120`: `DrawQuad()` constructs four `TexturedVertex2D` values and invokes `Action<TexturedVertex2D>` four times.
- `Graphics/Rendering/Vertices/TexturedVertex2D.cs`: the vertex contains 15 floats, including its draw depth. The current format therefore carries 60 bytes per vertex and 240 bytes per quad, excluding indices and backend allocation overhead.
- `Graphics/OpenGL/Batches/GLVertexBatch.cs` and `Graphics/Veldrid/Batches/VeldridVertexBatch.cs`: each `Add()` checks the active batch, buffer availability/capacity and vertex equality.
- Their vertex buffers also obtain CPU/staging memory and maintain usage state per vertex.
- `Graphics/Rendering/Deferred/DeferredVertexBatch.cs:93`: four calls assemble a quad into a temporary primitive before recording one upload event.

### Proposed first implementation

Introduce an internal, invariant primitive-submission interface for complete quads. Keep the public `IVertexBatch<in TVertex>` contract and explicit custom `vertexAction` behaviour intact. A span-based method should not simply be added to the existing contravariant interface.

The default `DrawQuad()` path would prepare the same four vertices in the same order and submit them together. Backend implementations would select the active batch, obtain memory and resolve capacity once per primitive, while retaining the existing per-vertex equality and dirty-range rules. GL, ordinary Veldrid, Metal, Deferred and Dummy need explicit handling.

Constraints:

- Preserve bottom-left, bottom-right, top-right, top-left order, indices, colour interpolation, atlas padding, independent texture coordinates and per-quad depth.
- Do not allow a complete primitive to be accidentally split across incompatible buffer ranges. Account for existing single-vertex callers and partially filled batches.
- Preserve GL buffer rotation and Veldrid's no-wrap behaviour. `CreateQuadBatch()` arguments are in quads, but backend `Size` conventions differ.
- Keep backend first-initialisation behaviour and dirty detection; an all-zero vertex cannot be assumed to represent initialised GPU storage.
- The explicit callback path may intentionally alter vertices and must not be bypassed.

This is a CPU submission optimisation. Four vertices still reach the GPU. Instancing could reduce repeated attributes later, but needs new layouts, shaders and renderer support; it is a separate project.

## 2. Cursor trails

### Observed path

Client source: `../g0v0/osu.Game.Rulesets.Osu/UI/Cursor/CursorTrail.cs`.

- The source and each draw node hold 2048 `TrailPart` slots.
- `Update()` invalidates the draw node every update to advance its fade clock.
- `TrailDrawNode.ApplyState()` copies the whole array, even when no points were added.
- `Draw()` traverses all slots and constructs four rotated vertices for each slot that passes the existing conditions.
- Each slot already has an `InvalidationID`, changed on insertion and time rebasing.
- `../g0v0-resources/osu.Game.Resources/Shaders/sh_CursorTrail.vs` already calculates fading on the GPU.

### Staged design

1. Separate point-data changes from clock-only changes. Track insertion sequence and a time-rebase generation, with a separate last-seen sequence per draw node.
2. On `ApplyState()`, copy only newly written ring ranges. If the draw node missed at least a whole ring, or time was rebased, copy the full snapshot. Never read live source slots from the draw thread.
3. Cache local geometry by slot version and the relevant style state. Rotation, origin, cursor scale, per-part scale, texture display size/coordinates and colour must be accounted for. The drawable matrix remains applied through the existing local-matrix path.
4. Keep the dynamic backend for the first change. A later GPU-retained path needs stable slot addressing and dirty-range uploads with proper GPU execution lifetimes.

### Behaviour that must remain intact

- Current rendering visits physical slots from index 0 upwards, not oldest-to-newest ring order. Reordering overlapping translucent sprites changes output. `LegacyCursorTrail` uses inherited blending for disjoint trails and additive blending for continuous trails.
- CPU skipping uses `time - part.Time >= 1`; shader alpha reaches zero when `time >= part.Time`. These conditions differ by one normalised time unit. Preserve the CPU predicate initially.
- Zero vertex alpha alone does not prove a draw is irrelevant: `sh_Masking.h` can output border colour independently of content alpha, and blend/depth modes also matter.
- Do not permanently delete expired slots solely under an assumption that time is monotonic. The existing snapshots, time rebasing and presentation-clock integration must remain valid.
- Preserve the fork's live-clock versus `ReplayExportPresentationClock` selection.
- A full 2048-slot GPU draw would retain expired vertex/fragment work; it is not automatically faster than the current compact submission.
- `StaticQuadMesh<T>` is internal to the framework and immutable. It cannot be reused as a mutable client-side ring by overwriting in-flight buffers. Do not recreate a whole static mesh on every mouse event.

Expected scope: initial changes reduce copying and vertex reconstruction. Ordinary GL/Veldrid already attempt to skip unchanged vertex uploads, so existing upload savings must not be claimed as new. Deferred currently copies all submitted primitives.

## 3. Texture upload scheduling

Implementation status: empty dequeue attempts now use a volatile pending count published under the existing GL/Veldrid queue locks. Non-empty dequeue, actual uploads and the locked `UploadComplete` getter retain their existing paths. Global scheduling now uses a FIFO queue and reference-identity membership set under one lock, released before calling `Upload()`. The design below records the original proposal; the linked implementation documents describe the delivered changes and limitations.

### Observed path at research time

- `Graphics/Rendering/Renderer.cs:807`: ordinary textured drawing calls `texture.NativeTexture.Upload()` before native binding deduplication. A white-pixel-on-atlas shortcut is an exception.
- `Graphics/OpenGL/Textures/GLTexture.cs` and `Graphics/Veldrid/Textures/VeldridTexture.cs`: attempting to dequeue an upload locks the texture queue even when empty.
- `Renderer.cs:899`: global scheduling calls `ConcurrentQueue.Contains(texture)` before enqueueing, which searches pending entries. The check and enqueue are separate operations.
- Per-texture queues already suppress repeated global scheduling while their local queue remains non-empty. Global duplication is therefore not necessarily common.
- `IsQueuedForUpload` exists as an unused concrete property in GL/Veldrid/Dummy texture classes; it is not an atomic state protocol or part of `INativeTexture`.

### Proposed design

For the empty path, use a published pending count or flag maintained under the existing texture queue lock. A volatile read can avoid acquiring that lock when there is no published work. Preserve the draw-thread contract, and update the state on enqueue, dequeue, `FlushUploads()` and disposal. Do not read ordinary `Queue.Count` without synchronisation or assume a once-per-frame upload check is sufficient: new atlas data can arrive between two draws.

For global membership, a renderer-owned queue and reference-identity set under one short lock can make membership and insertion atomic:

- Enqueue updates both structures under the renderer queue lock.
- Dequeue removes membership under that lock, then releases it **before** calling `texture.Upload()`.
- Direct on-bind uploads do not remove membership while a physical global-queue entry still exists.
- Queue counts, frame budgets, initialisation and disposal/flush paths retain explicit handling.

Lock ordering is essential. `SetData()` currently holds the texture queue lock while scheduling globally. The renderer must never hold its queue lock while entering `Upload()` or acquiring a texture queue lock. A naked boolean replacement would not address races or lost scheduling.

This targets CPU locking/bookkeeping, not texture bandwidth. The global membership change is more relevant during loading or atlas growth than stable gameplay.

## 4. Deferred event coalescing

### Observed path

- `Graphics/Rendering/Deferred/Events/AddPrimitiveToBatchEvent.cs`: every primitive appends a resource reference and allocates/copies one memory region.
- `Graphics/Rendering/Deferred/Allocation/ResourceAllocator.cs`: regions come from pooled byte arrays with a 2 MiB minimum block. This does **not** mean a fresh byte array is allocated per primitive; there is still per-primitive bookkeeping and copying.
- `EventProcessor` traverses the event stream for uploads, then again for render state and draws.
- `VertexManager.Write()` handles one primitive at a time and `Draw()` independently reconstructs buffer positions and stride alignment.

### Proposal and boundary rules

A completed quad can first avoid the four-step temporary assembly described in section 1. To additionally reduce event count, coalesce adjacent same-batch primitive data into runs and cache the batch's resource reference for the recording frame.

A run must not cross a state/flush event, vertex type, allocator block, static mesh draw or frame boundary. Bulk writes must split into complete primitives at GPU buffer limits, with writer and draw cursor advancement agreeing exactly. Merely passing a longer memory region to the current single-primitive writer is unsafe.

Do not remove the two-phase upload ordering; it supports valid uniform ranges and buffer uploads before their use. Use bounded staging storage rather than unbounded per-batch retained arrays.

The current temporary primitive array and counter are static per vertex type. Any redesign should make mutable assembly state batch- or context-owned so independently drawing renderers cannot share it. This is a source-level concurrency concern, not a reproduced live-render/export failure.

## 5. Blur and buffered effects

### Existing savings

`../g0v0/osu.Game/Graphics/Backgrounds/Background.cs` already uses `cachedFrameBuffer: true`, `RedrawOnScale = false` and sigma-dependent downsampling. A settled static background does not regenerate the blur every frame. Blanket extra caching or lower resolution would duplicate existing work or change appearance.

### Remaining opportunities

**CPU kernel radius:** `BufferedContainer.DrawNode.ApplyState()` calls `Blur.KernelSize()` for both axes even if sigma is unchanged. `Utils/Blur.cs` finds the threshold through repeated Gaussian evaluation. Cache by sigma without changing its threshold comparisons; replacing it with a closed-form approximation risks a different radius near boundaries.

**GPU weights:** `Resources/Shaders/sh_Blur.fs` computes Gaussian exponentials and normalisation per fragment, even though they depend on sigma/radius rather than pixel coordinates. A small uniform weight table could compute these once per kernel change. The existing shader uses a centre tap and paired samples at 1.5, 3.5, ..., up to 199.5, with a break after processing a pair. Preserve that sampling scheme rather than substituting another Gaussian kernel.

At the maximum radius, 101 weights fit in 26 `vec4` entries (416 bytes before other parameters). CPU-generated weights can remove repeated exponentials but introduce uniform traffic and dynamic indexing. `mediump` shader arithmetic and CPU floating-point evaluation can differ; visual equivalence and actual GPU benefit require later authorised runtime evaluation. Texture sampling may remain the bottleneck. Preserve the existing Apple-device shader workaround.

**Content/effect versions:** changing blur or grayscale currently triggers the whole buffered pass, including redrawing children. Separate versions could reuse unchanged main-buffer content while rerunning effects. This requires size, scale, clipping and child-update invalidation to be correct. Background blur animations sometimes change downsample scale, requiring main-buffer recreation anyway. This is a broader redesign, not a trivial `RequiresRedraw` early return.

## Resource-lifetime follow-up

`Graphics/Veldrid/Textures/VeldridTexture.cs:505` constructs a new `VeldridTextureResources` wrapper on every ordinary upload, including when the texture and sampler are unchanged. The wrapper caches a `ResourceSet`. Replacing the wrapper can discard that cache; if a set was already created, the replacement path has no explicit set-only retirement. This is a potential resource-lifetime issue, not a measured leak.

Reuse is worth investigating when texture/sampler identity is unchanged. On resize or sampler change, ownership must be explicit: calling `Dispose()` on a replaced wrapper currently also disposes its texture and sampler, which may be shared with the replacement. Capture old handles for delayed retirement and preserve prior recorded command references. Consider sampler changes, mip levels, current pipeline attachments and Deferred's texture references before implementing. Video textures have a separate override that already retains their plane wrappers; they are not evidence of per-frame churn in this ordinary upload path.

## Deprioritised approaches

- **More D3D11 binding caches:** `../g0v0-veldrid/src/Veldrid/D3D11/D3D11CommandList.cs:211,1056` already skips equal resource-set/dynamic-offset bindings and equal graphics pipelines. Further framework caching may save dispatch but must not suppress `GetResourceSet()` uploads or overlook pipeline/framebuffer invalidation.
- **Remove all local batches:** composites already flatten where allowed. Local batches trade extra flushes against stable vertex positions and upload reuse. Global merging can lower draw count while increasing CPU comparison and upload traffic. Capacity and ownership changes need workload evidence.
- **Make all sprites static GPU meshes:** transforms, colours, masking and backbuffer depth are dynamic, while ordinary backends already perform equality-based reuse. Per-object static draws can fragment batching. The successful slider approach does not transfer unchanged to ordinary sprites.
- **Restore region-only mipmap generation:** both texture backends explicitly disabled it after cross-platform regressions. Do not re-enable it as a bookkeeping optimisation.

## Next implementation boundary

The whole-quad submission patch has now been implemented with unchanged shaders and existing batch synchronisation. Cursor snapshot/geometry reuse remains the recommended separate next patch. Keeping those patches separate makes attribution and rollback possible. No FPS percentage or cross-backend correctness claim follows from this source research or the subsequent client compilation.
