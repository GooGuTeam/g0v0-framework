# Framework documentation

Design notes, experiment records and measurements for g0v0!framework. Dated files are records of a single investigation; the others describe the current design.

## Building

Builds restore the published `g0v0.Veldrid` packages. Unreleased renderer changes need a sibling `g0v0-veldrid` checkout and `-p:G0V0SourceVeldrid=true` (see `Directory.Build.targets`). Native libraries are described in [native/README.md](../native/README.md).

## Reviews

- [local-change-review.md](local-change-review.md): review of the local framework work (2026-10-28), including the WASAPI thunk fixes and the image codec release blocker

## Rendering pipeline

- [rendering-cpu-optimisation.md](rendering-cpu-optimisation.md): CPU-side draw submission work
- [quad-submission-optimisation.md](quad-submission-optimisation.md): whole-quad submission
- [untextured-white-pixel.md](untextured-white-pixel.md): untextured box drawing
- [masking-batching.md](masking-batching.md), [masking-interior-retention.md](masking-interior-retention.md), [per-vertex-masking.md](per-vertex-masking.md): masking
- [fragment-shader-mobile-optimisation.md](fragment-shader-mobile-optimisation.md): masking and blur shader cost
- [shader-binary-cache-and-warmup.md](shader-binary-cache-and-warmup.md): shader binary cache and warm-up
- [slider-rendering-optimisation.md](slider-rendering-optimisation.md), [slider-direct-rendering-research.md](slider-direct-rendering-research.md): slider meshes
- [rendering-next-steps-research.md](rendering-next-steps-research.md), [gpu-driven-pipeline-research.md](gpu-driven-pipeline-research.md), [bindless-texture-research.md](bindless-texture-research.md): research

## Textures and atlases

- [texture-slots.md](texture-slots.md), [atlas-pages-and-descriptor-arrays.md](atlas-pages-and-descriptor-arrays.md), [atlas-uv-cache-optimisation.md](atlas-uv-cache-optimisation.md)
- [texture-upload-queue-optimisation.md](texture-upload-queue-optimisation.md), [texture-empty-upload-optimisation.md](texture-empty-upload-optimisation.md), [texture-upload-ownership-correctness.md](texture-upload-ownership-correctness.md)
- [region-mipmap-generation.md](region-mipmap-generation.md)

## Text and fonts

- [ascii-hd-fonts.md](ascii-hd-fonts.md), [draw-overhead-and-ascii-threshold.md](draw-overhead-and-ascii-threshold.md)
- [font-atlas-and-texture-size-probe.md](font-atlas-and-texture-size-probe.md), [font-atlas-capacity-optimisation.md](font-atlas-capacity-optimisation.md), [font-atlas-packing-optimisation.md](font-atlas-packing-optimisation.md), [font-atlas-precision.md](font-atlas-precision.md)
- [glyph-and-deferred-reference-performance.md](glyph-and-deferred-reference-performance.md), [text-submission-optimisation.md](text-submission-optimisation.md), [text-motion-optimisation.md](text-motion-optimisation.md)

## Video, recording and capture

- [offline-video-export.md](offline-video-export.md), [vulkan-recording-streaming-design.md](vulkan-recording-streaming-design.md), [gpu-frame-capture.md](gpu-frame-capture.md)
- [media-foundation-video-decoding.md](media-foundation-video-decoding.md)

## Vulkan and presentation

- [vulkan-compatibility.md](vulkan-compatibility.md), [vulkan-pipeline-cache-2026-10-09.md](vulkan-pipeline-cache-2026-10-09.md)
- [vulkan-fse-experiment.md](vulkan-fse-experiment.md), [low-latency-presentation-experiment.md](low-latency-presentation-experiment.md)
- Records: [vulkan-optimisation-2026-10-08.md](vulkan-optimisation-2026-10-08.md), [vulkan-validation-2026-10-08.md](vulkan-validation-2026-10-08.md), [vulkan-validation-fixes-2026-10-08.md](vulkan-validation-fixes-2026-10-08.md), [vulkan-test-results-2026-10-08.md](vulkan-test-results-2026-10-08.md), [vulkan-descriptor-experiment-2026-10-09.md](vulkan-descriptor-experiment-2026-10-09.md)

## Profiling and benchmarks

- [benchmark-matrix.md](benchmark-matrix.md)
- [profiling-bottlenecks-2026-10-26.md](profiling-bottlenecks-2026-10-26.md), [profiling-hitches-2026-10-26.md](profiling-hitches-2026-10-26.md)
- [android-performance-research.md](android-performance-research.md)
