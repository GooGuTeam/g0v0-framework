# Vulkan validation fixes and retest

## Changes (sibling `veldrid` source)

- Optimal images and their tracked initial layouts now start as UNDEFINED instead of PREINITIALIZED. Initial transitions accept UNDEFINED for sampled/storage textures.
- Swapchain image wrappers no longer clear images on construction. Initial clearing is deferred until that particular image has been acquired and its acquire fence waited on. Tracking resets when the swapchain is recreated.
- CopyBuffer's transfer-write dependency now covers all subsequent reads/writes, not only vertex attributes. This covers index/uniform/storage consumers as well. This is conservative; further narrowing requires usage-aware analysis and benchmarks.
- Render-pass attachment dependencies include early/late depth-stencil accesses and previous memory accesses. Framebuffer and pipeline creation share the same dependency factory to preserve render-pass compatibility.
- Presentation waits on a binary semaphore signalled by an empty graphics queue submission after preceding rendering/layout transitions. Semaphores are per swapchain image, so reuse is tied to image reacquisition rather than only a graphics fence. They are recreated with the swapchain.
- Explicit WaitForIdle now waits for the device, not only the graphics queue, covering a separate presentation queue during swapchain replacement. No per-frame WaitForIdle was added to the ordinary rendering path.
- Removed obsolete device layer names and unused bookkeeping; instance validation layers remain active.
- Existing opt-in FSE work is retained. This retest used FSE=0.

Build using `-p:UseLocalVeldrid=true`; default package builds do NOT contain these fixes. The game executable was not rebuilt during this fix pass; the validation target was Framework.Tests.

## Final validation runs

Khronos 1.4.363.0, core + synchronization validation, RTX 5060 Laptop GPU / NVIDIA 591.91:

| Run | Scenes / steps | Validation errors | Warnings | Exit |
|---|---|---|---|---|
| veldrid-20261008-232300 | 19 / 122 | 0 | 4 ShaderOutputNotConsumed | 0 |
| deferred-20261008-232347 | 19 / 122 | 0 | 4 ShaderOutputNotConsumed | 0 |

Logs under `%TEMP%\g0v0-vulkan-validation\<run>\`.

The first intermediate build introduced render-pass dependency incompatibility because the pipeline's compatibility render pass still used the old dependency. This was corrected by sharing the factory, and is absent from the final runs.

The four shader-output warnings remain; they are not hidden or filtered. Passing the selected tests does not establish exhaustive pixel correctness, all application scenarios, FSE correctness, performance parity or multi-GPU correctness.

## Video test limitation

Additional Deferred Vulkan video validation run: `deferred-20261008-232503`.

No Vulkan validation errors/warnings were logged, but the test stopped on a `decoding ran` timeout in `TestSceneVideo.TestDecodingStopsBeforeStartTime(bool)`, line 176. The launcher detected the failed step and closed the window; exit code 0 is NOT a test pass. The video suite did not finish, and decoder/timing issues remain unresolved. Validation overhead is a possible contributor but has not been established as the cause.

VeldridTextureUploadTest + RendererTest: 11 passed. Build succeeded (existing NuGet vulnerability/platform warnings remain). `git diff --check` passed in the Veldrid checkout.

## Follow-up

Rebuild g0v0 explicitly with the local dependency before testing the client. Repeat FSE acquire/release, resize/minimise/restore, and long-duration tests with validation. Compare video tests against D3D11 to isolate decoder behaviour. Benchmark conservative barriers without validation before making performance claims.
