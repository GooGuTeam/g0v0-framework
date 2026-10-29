# Vulkan texture upload ordering

## Cause and fix

`VeldridRenderer` previously recorded span uploads into `textureUploadPipeline` and pointer uploads (including ordinary texture and video uploads) into `bufferUpdatePipeline`. Both were submitted before the frame's graphics commands. A texture updated between two recorded draws could therefore expose the later contents to the earlier draw. Mipmap generation and regional copies were also recorded separately from the uploads they depended on.

The Vulkan backend tracks image layouts during command recording. Vulkan's [submission and synchronization rules](https://docs.vulkan.org/spec/latest/chapters/synchronization.html) do not make the order of host calls across separately submitted command buffers an execution dependency.

Both upload overloads now record Vulkan copies into `graphicsPipeline`, alongside drawing and mipmap generation. The existing Vulkan copy implementation ends an active render pass and records the required image barriers. The existing staging pool already tracks graphics execution fences, so staging memory remains retained until those copies complete. No GPU idle wait or additional submission is introduced in the upload path. Other backends retain their existing pipeline selection; the deferred renderer already records both upload overloads into its graphics pipeline.

## Verification (Windows, 2026-10-29)

`VulkanTextureUploadGpuTest` alternates uploads and draws within one frame, explicitly records each draw before the next upload, and checks the resulting GPU pixels after frame completion. Each later upload changes only half of the texture, so the assertions check both ordering and preservation of the untouched half. Six cases cover pointer/span uploads, level-zero sampling, whole-texture mipmaps and regional mipmaps.

- Original implementation: **6 failed / 6**, at the pixel assertions.
- Fixed implementation: **6 passed / 6**.
- Existing upload ownership, alpha conversion, row-stride and mipmap-region tests: **30 passed / 30**.
- Desktop Debug solution build with warnings as errors: **0 warnings, 0 errors**.

Build command (local Veldrid source dependency):

```powershell
dotnet build -c Debug -warnaserror osu-framework.Desktop.slnf --no-restore -m:1 -nr:false -p:G0V0SourceVeldrid=true
```

Run the explicit GPU fixture separately; combining it with non-explicit fixtures can omit it in NUnit:

```powershell
dotnet test osu.Framework.Tests/bin/Debug/net10.0/osu.Framework.Tests.dll --no-build --filter "FullyQualifiedName~VulkanTextureUploadGpuTest"
dotnet test osu.Framework.Tests/bin/Debug/net10.0/osu.Framework.Tests.dll --no-build --filter "FullyQualifiedName~VeldridTextureUploadTest|FullyQualifiedName~ArrayPoolTextureUploadTest|FullyQualifiedName~AlphaTextureUploadTest|FullyQualifiedName~MipmapRegionsTest"
```

Both GPU runs used Khronos validation layer 1.4.363.0 with `tools/vk_layer_settings.txt`; the logs confirm core and synchronization validation were enabled. Neither run reported `SYNC-HAZARD` errors. **Neither run was fully validation-clean**: each reported six `VUID-vkDestroyDevice-device-05137` errors for existing offline-renderer teardown leaks (10 objects per device). This teardown finding is separate from the pixel-ordering regression and is not fixed here.

Tests ran outside the sandbox after the sandboxed test host stalled during startup. No Linux/macOS run, windowed playback stress test or performance benchmark was performed.
