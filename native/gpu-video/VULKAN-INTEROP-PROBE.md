# Vulkan/D3D11 interop validation

`test_vulkan_interop.cpp` is an isolated native probe, not the game's Vulkan recording backend.

Build:

```powershell
./build-vulkan-probe.ps1 -Zig C:/path/to/zig.exe -VulkanInclude C:/path/to/Vulkan-Headers/include
```

Tested with Zig 0.14.1, Khronos Vulkan-Headers v1.3.290. Headers were downloaded only into temporary tooling storage, not vendored. Requires the installed Windows Vulkan loader and graphics drivers. No external network connection or game settings modification occurs during execution.

## Veldrid integration preparation

Local Veldrid now has `VulkanDeviceOptions.EnableWin32CaptureInterop`: it negotiates the entire Win32 external-memory/keyed-mutex bundle plus Vulkan 1.0 instance dependencies without making them mandatory for ordinary rendering. `BackendInfoVulkan.Win32CaptureInteropEnabled` reports what was actually enabled, not image-format support. Framework local builds request this only with `G0V0_VULKAN_CAPTURE_INTEROP=1`.

`BackendInfoVulkan.SubmitExternalCapture` submits a caller-owned native command buffer/pNext chain/fence under Veldrid's graphics queue lock after ensuring image acquisition. It does not recycle or own native resources; callers must restore borrowed image layouts, use same-device handles and drain before destruction. This is low-level interop, not a recorder.

An explicit hardware test passed: requested bundle negotiated true on the selected local adapter, default device reports false, and submissions on a non-enabled device are rejected. No real native command buffer has yet been tested through this new submission entry point. Build passed with warnings as errors.

## Actual local results

Both adapters passed 10000 alternating red/green frames, each with keyed-mutex acquire/release and pixel comparison:

- NVIDIA GeForce RTX 5060 Laptop GPU, driver 591.91.
- Intel(R) Graphics, driver 101.8826.

The test matches Vulkan physical-device LUID to DXGI adapter LUID; creates a BGRA8 NT-handle/keyed-mutex D3D11 texture; verifies Vulkan D3D11_TEXTURE external-image import capability; imports dedicated memory; transfers external queue ownership/layout around the Vulkan clear; hands off key 0 -> 1 to D3D11; verifies the pixel then hands key 0 back. NT handles are closed after import. Successful paths explicitly destroy resources.

The D3D11 staging copy/map is ONLY the test oracle. It is not part of the proposed production capture/encode path. This proves shared memory/synchronisation correctness for the tested drivers and format, not GPU-only hardware encoding, zero driver-internal traffic, or latency targets.

Limitations: the installed system has no Khronos validation layer (vulkaninfo lists NVIDIA layers only), so this run has NOT passed Vulkan validation. Failure cleanup relies on isolated process teardown. The test does not cover device loss, resizing, deliberate long stalls, games' existing queue submissions, video encode or AMD. These must be covered before promoting the experiment into a production backend.
