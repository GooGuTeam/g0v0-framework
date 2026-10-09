# Vulkan usage optimisation — 2026-10-08

## Implemented

1. `VeldridDevice` remembers the drawable size used to create the swapchain. The first frame no longer calls ResizeMainWindow for an unchanged size. A genuine size change still resizes normally. This avoids a redundant Vulkan swapchain recreation/device-idle wait on that path.
2. `VeldridShader` does not synthesise unused fragment colour outputs for Vulkan. It still invokes WithPassthroughInput with an empty attribute list to expand required shader template placeholders. Other backends retain the compatibility workaround. Simply skipping the method was tested and failed GLSL compilation; that intermediate attempt was corrected.
3. Local Veldrid's CopyBuffer barrier is now a VkBufferMemoryBarrier scoped to the destination buffer and copied byte range. Destination stages/accesses are derived from BufferUsage: vertex/index input, uniform/storage shader access, indirect arguments, transfer and host access as applicable. Optional shader stages are guarded by device features. This replaces the earlier global memory barrier targeting AllCommands; actual execution-stage narrowing depends on the buffer usage.

No per-frame idle wait, disabling of SSBO, or removal of required present synchronization was introduced. Render-pass dependencies remain conservative. Further semaphore/submission changes were deliberately deferred pending profiling.

## Verification

RTX 5060 Laptop GPU, NVIDIA 591.91, Khronos 1.4.363.0 core + synchronization validation, FSE off:

| Run | Scenes / logged steps | Validation errors / warnings | Exit |
|---|---|---|---|
| veldrid-20261008-234124 | 19 / 122 | 0 / 0 | 0 |
| deferred-20261008-234211 | 19 / 122 | 0 / 0 | 0 |

Artifacts: `%TEMP%\g0v0-vulkan-validation\<run>\`.
The prior four ShaderOutputNotConsumed warnings are absent without message filtering.

Related unit tests (VeldridTextureUploadTest, RendererTest, ShaderRegexTest, ShaderStorageBufferObjectStackTest): 27 passed.
Framework.Tests and g0v0 Desktop built successfully using `-p:UseLocalVeldrid=true`; existing package vulnerability warnings remain. Desktop was rebuilt but not launched for an additional gameplay pass in this optimisation round.

## Scope and limitations

These are targeted usage improvements, not measured FPS/latency claims. No controlled A/B timing benchmark has been performed. Validation-enabled timing is not representative of normal gameplay performance. Hardware coverage is limited to this NVIDIA machine, with no FSE regression test in this round.

Framework changes apply to ordinary and deferred backends. The CopyBuffer backend change requires the sibling local Veldrid checkout and `UseLocalVeldrid=true`; the packaged dependency does not contain it. All previous fixes/FSE work and unrelated user edits are retained.
