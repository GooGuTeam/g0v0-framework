# Vulkan test results — 2026-10-08

## Environment

Windows, NVIDIA GeForce RTX 5060 Laptop GPU, NVIDIA driver 591.91.
Built Framework.Tests with `-p:UseLocalVeldrid=true`, using sibling Veldrid based on a405fe8484 plus the local opt-in FSE prototype. These tests used `VELDRID_VK_FSE=0` and `OSU_GRAPHICS_SURFACE=Vulkan`; ordinary and deferred renderers were selected separately with `OSU_GRAPHICS_RENDERER`.

An existing g0v0 process remained running. These are functional smoke tests, not controlled performance measurements.

## Windowed visual test runner

`--benchmark --test-filter=TestSceneScissor,TestSceneStencil,TestSceneShaderStorageBufferObject,TestSceneMasking,TestSceneBlending,TestSceneComplexBlending,TestSceneDynamicDepth,TestSceneBufferedContainer,TestSceneCachedBufferedContainer,TestSceneFrontToBackBufferedContainer,TestSceneTexturePremultiplication,TestSceneTextureCropping,TestSceneTexturedTriangle,TestSceneDrawNodeDisposal,TestSceneGammaCorrection`

Filtering is substring-based, so performance and related buffered-container scenes are also included. RunAllSteps was enabled.

Both ordinary Veldrid Vulkan and Deferred Vulkan completed 19 scenes / 122 logged steps, with no logged step errors and empty stderr:

- Blending, BlendingPerformance
- BufferedContainer, BufferedContainerClipping, BufferedContainerView
- CachedBufferedContainer, FrontToBackBufferedContainer
- ComplexBlending, DrawNodeDisposal, DynamicDepth
- GammaCorrection, Masking, MaskingPerformance
- Scissor, ShaderStorageBufferObject, Stencil
- TextureCropping, TexturePremultiplication, TexturedTriangle

Completion is not a pixel-correctness guarantee: some scenes primarily display examples and do not assert framebuffer contents. No screenshot baseline comparison was performed. Short performance-scene completion is not a sustained stress test.

## Deferred video retest

`--benchmark --test-filter=TestSceneVideo`

TestSceneVideo completed through step 281; TestSceneVideoLayout also completed. The earlier VP8 end-of-playback seek timeout did not reproduce in this run. This does not prove it fixed or identify its cause; the previous run and this one are not a controlled A/B comparison.

Decoder issues remain visible: one hardware-decoding fallback message, 100 failed packet-send messages, six failed receive messages, and 119 stderr lines including missing-reference/decode warnings. No logged test-step failure or DeviceLost. These decoder diagnostics are not by themselves evidence of a Vulkan driver fault.

## Unit tests

VeldridTextureUploadTest and RendererTest: 11 passed, zero failed.

## Validation coverage

Directly enumerated installed Vulkan instance layers via vulkan-1.dll:

- VK_LAYER_NV_optimus
- VK_LAYER_NV_present

VK_LAYER_KHRONOS_validation is not installed. No validation-layer or synchronization-validation pass was performed. Therefore absence of API validation errors has NOT been established.

## Artifacts

`%TEMP%\g0v0-vulkan-matrix\`

- veldrid-stdout.log / veldrid-stderr.log
- deferred-stdout.log / deferred-stderr.log
- video-retest-stdout.log / video-retest-stderr.log
- status.log (runner completion/timeout tracking; exit codes were not captured successfully)

## Next coverage gaps

Install Khronos validation layers with consent, enable validation and synchronization validation, and repeat. Add deterministic framebuffer readback assertions for clipping/blending/mipmap upload. Repeat video seek under both Vulkan and a non-Vulkan backend to isolate decoder timing problems. Test resize, minimize/restore, VSync changes, monitor switching and long-duration load separately, then repeat with FSE enabled.
