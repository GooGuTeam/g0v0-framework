# Khronos core and synchronization validation — 2026-10-08

## Setup

Extracted the official LunarG Windows x64 SDK 1.4.363.0 locally; did not run its installer, replace the system Vulkan loader, or register layers globally.

Source: https://sdk.lunarg.com/sdk/download/1.4.363.0/windows/vulkansdk-windows-X64-1.4.363.0.exe

SHA-256 verified against LunarG files.json:
`94a82d378f7a5e3e54c9db7d2fb7016af136e14ac0a18dbf0f2f67a36352d141`

Layer directory: `%LOCALAPPDATA%\g0v0-tools\vulkan\1.4.363.0\Bin`.

Run from framework root:

```powershell
# Build first. Keep UseLocalVeldrid consistent across restore/build.
dotnet build osu.Framework.Tests/osu.Framework.Tests.csproj -p:UseLocalVeldrid=true
powershell -NoProfile -File tools/RunVulkanValidation.ps1 -Renderer veldrid
powershell -NoProfile -File tools/RunVulkanValidation.ps1 -Renderer deferred
```

The launcher sets process-local VK_LAYER_PATH, VK_INSTANCE_LAYERS, VK_LAYER_SETTINGS_PATH and loader diagnostics, then restores the caller's environment. `tools/vk_layer_settings.txt` enables synchronization validation, full submit-time validation, record-time reporting and stdout diagnostics. Core validation remains enabled. FSE is explicitly off. No GraphicsDeviceOptions.Debug change is needed: the loader injects the layer, which emits its own diagnostics (avoiding the backend's throwing debug callback).

## Activation evidence

Loader stderr confirms loading VkLayer_khronos_validation.dll and inserting the layer into both instance and device call chains. Layer stdout explicitly reports:

```
CURRENT-VALIDATION-ENABLED
  - Core Checks
  - Synchronization
  - Stateless Parameter
  - Object lifetime
  - Thread Safety
  - Handle Wrapping
```

## Results: NOT validation-clean

RTX 5060 Laptop GPU / NVIDIA 591.91, local Veldrid based on a405fe8484 with the FSE prototype disabled.
Both renderers completed 19 scenes / 122 logged steps, but emitted validation errors:

| Diagnostic | Ordinary Vulkan | Deferred Vulkan |
|---|---:|---:|
| VUID-VkDeviceCreateInfo-ppEnabledLayerNames-12385 | 1 | 1 |
| VUID-VkImageCreateInfo-initialLayout-12478 | 10 | 10 |
| UNASSIGNED-non-acquired-swapchain-image-used | 9 | 9 |
| SYNC-HAZARD-WRITE-AFTER-WRITE | 10 | 10 |
| SYNC-HAZARD-READ-AFTER-WRITE | 10 | 8 |
| SYNC-HAZARD-PRESENT-AFTER-WRITE | 10 | 10 |
| Total emitted error messages | 50 | 48 |

Four ShaderOutputNotConsumed warnings were also emitted per renderer. The layer suppresses duplicate messages after 10 occurrences per diagnostic; counts are logged messages, NOT total runtime violations.

Representative findings:

- Device creation passes a non-null legacy ppEnabledLayerNames; the current layer flags this against the latest specification. Treat this compatibility/legacy-API diagnostic separately from demonstrated synchronization hazards.
- Optimal-tiled images use PREINITIALIZED initial layout; the current layer requires LINEAR for that initial layout.
- Swapchain images are transitioned before acquisition.
- A render-pass depth clear is not synchronized with the attachment layout transition; depth/stencil write access at early fragment tests is missing from the reported dependency.
- Index reads after CopyBuffer are not correctly synchronized. The reported destination permits vertex attribute input but does not cover index input.
- Presentation is not sufficiently synchronized with the preceding image layout transition.

These are diagnostic findings, not yet fixes. Validation errors do not prove each symptom is NVIDIA-specific; the tests provide concrete backend issues to investigate. This was not a FSE validation run, GPU-assisted validation run, or exhaustive shader-access synchronization proof.

## Artifacts

`%TEMP%\g0v0-vulkan-validation\veldrid-20261008-221056\`

`%TEMP%\g0v0-vulkan-validation\deferred-20261008-221213\`

Each contains stdout.log, stderr.log and status.txt. Initial launcher exit codes were not captured (blank); scene completion and error counts above come from the logs. The launcher subsequently gained process-handle retention and an explicit unavailable marker for exit-code reporting.
