# Masking state coalescing

## Problem and result

Masking push/pop previously flushed geometry immediately, including a child-to-parent-to-child transition where no parent geometry was drawn and both children had identical clipping. Scissor updates introduced the same barrier. Comparing the whole `MaskingInfo` also treated CPU-only culling metadata as a shader change.

The renderer now keeps logical clipping state separately from the state attached to queued geometry. It applies pending clipping immediately before adding new vertices. Intermediate changes that return to the same shader parameters and effective scissor do not flush. The regression with 100 equivalent sibling scopes submits all 400 vertices in one batch.

Actual differences in masking shader parameters, masking enablement or effective scissor still flush before the new vertices are appended. This does not batch arbitrary different masks into a single draw. That would require a per-primitive mask index and GPU mask storage, plus shader/custom-vertex compatibility and separate hardware-scissor handling. No such shader ABI change is made here.

## Correctness boundaries

- Logical `CurrentMaskingInfo`, `IsMaskingActive` and `Scissor` update immediately for CPU callers.
- Batch flushes use the applied masking snapshot, even after the logical stack has been popped. GL, Veldrid/Metal and Deferred batch draw paths use `DrawBatchVertices` for this purpose.
- Direct draws apply pending state; static meshes do so before binding their own buffers, after flushing existing geometry.
- Every mask push creates a corresponding scissor entry, even when the mask compares equal. Every pop removes it. This fixes the previous equal-mask early return, including identical default masks that still change shader masking enablement and explicit scissor overwrites.
- Effective scissor comparison uses viewport intersection and existing framebuffer UV-origin compensation. Viewport and framebuffer transitions invalidate the scissor cache. Old geometry is flushed before changing the viewport.
- `ConservativeScreenSpaceQuad` is CPU-only metadata. `ScreenSpaceAABB` is accounted for through the effective scissor; neither independently forces a uniform update. Public `MaskingInfo` equality remains unchanged.
- Vertex ordering, shader layouts, texture bindings and blending/depth boundaries are retained. No opaque/transparent sorting is introduced.

## Validation

Windows desktop Debug build with warnings as errors: **0 warnings, 0 errors**.

```powershell
dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf --no-restore -m:1 -nr:false -p:G0V0SourceVeldrid=true
```

The existing local source-dependency configuration is required by the working tree.

Focused masking/renderer/clipping suite: **20 passed**. It includes nine new batching/state regressions and existing renderer, scissor, masking and buffered-clipping tests. Existing visual scene tests run headlessly; they are not pixel-output tests.

Explicit GPU regression: **1 passed on Windows D3D11**. `MaskingBatchingGpuTest` renders overlapping translucent quads, rounded/hollow masks, scissor changes and a return to unmasked drawing into a framebuffer. It compares every pixel against an eager-state reference rendering and verifies non-black output. The GPU test is explicit because it requires a graphics device and readback.

```powershell
dotnet test osu.Framework.Tests/bin/Debug/net10.0/osu.Framework.Tests.dll --no-build --filter "FullyQualifiedName~MaskingBatchingTest|FullyQualifiedName~RendererTest|FullyQualifiedName~TestSceneScissor|FullyQualifiedName~TestSceneMasking|FullyQualifiedName~TestSceneBufferedContainerClipping"
dotnet test osu.Framework.Tests/bin/Debug/net10.0/osu.Framework.Tests.dll --no-build --filter "FullyQualifiedName~MaskingBatchingGpuTest.TestDeferredMaskChangesMatchEagerChanges"
```

No GL, Vulkan, Metal or Deferred GPU pixel comparison, Linux run, or frame-time benchmark has been performed. The single-batch count is from the recording-batch regression, not a measured application FPS improvement.

CodeFileSanity retains the existing `tools/Sdl3AudioSmoke/Program.cs` filename/type mismatch, apart from expected fork licence-header noise. InspectCode's pinned tool logs .NET toolchain compatibility exceptions; its findings are reviewed separately rather than treated as a clean full-repository check.
