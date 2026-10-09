# Low-latency presentation experiment

## Scope

Opt-in local Veldrid change: set `VELDRID_LOW_LATENCY=1` before device creation. Unset it (or set 0) for the baseline. Requires `-p:UseLocalVeldrid=true`; packaged Veldrid does not contain this change. Existing user modifications, including Vulkan FSE/synchronization changes, are retained.

This is a queue-depth/pacing experiment, not a measured input-latency improvement. No native thread migration or legacy DXGI presentation is introduced.

## Direct3D11

On DXGI 1.4+ with frame-latency waitable-object support, keep FLIP_DISCARD, two buffers, and MaximumFrameLatency=1. With the experiment enabled, allow FRAME_LATENCY_WAITABLE_OBJECT and ALLOW_TEARING flags together instead of removing the waitable object when the framework permits tearing. Present still uses tearing only with SyncInterval=0. Existing GameHost waits before obtaining the latest DrawNode snapshot after a rendered frame.

The wait remains bounded by the existing 1000 ms timeout. This is not proof of Independent Flip or a guarantee of latency. Test fullscreen/windowed transitions, minimized recovery, resize and VSync changes. The implementation creates a windowed DXGI swapchain; do not extend these flags blindly to DXGI exclusive fullscreen. Also dispose the owned wait handle during final swapchain disposal. Tearing creation flags are guarded by flip-discard support.

## Vulkan

With VSync enabled, use strict FIFO instead of preferring FIFO_RELAXED. With VSync disabled, retain the existing selection: IMMEDIATE when tearing is allowed and supported; otherwise MAILBOX, then IMMEDIATE, with FIFO fallback. The experiment requests max(2, surface minimum) images, clamped to the surface maximum, instead of minimum+1.

Image count is NOT a GPU frames-in-flight cap. Vulkan still acquires/waits for the next image in SwapBuffers; WaitForNextFrameReady is currently empty. No per-frame vkDeviceWaitIdle or vkQueueWaitIdle is added. Fewer images may reduce FIFO queue depth but can stall acquisition and reduce throughput, especially with MAILBOX. Drivers may return more images than requested. This patch does not implement present-wait/present-id extensions or a render-submission fence limiter.

## Build and compare (PowerShell, from workspace root)

```powershell
dotnet build g0v0/osu.Desktop/osu.Desktop.csproj -p:UseLocalVeldrid=true
$env:VELDRID_LOW_LATENCY = '1'
$env:VELDRID_VK_FSE = '0' # isolate the pacing experiment from FSE
$env:OSU_GRAPHICS_SURFACE = 'Vulkan' # repeat with 'Direct3D11'
# Launch the freshly built desktop executable using your normal isolated test profile.
# Compare with the same renderer, frame limit, VSync, resolution, driver and scene:
$env:VELDRID_LOW_LATENCY = '0'
# Restart the process for each environment change.
```

Compare both ordinary and deferred renderers, VSync on/off, windowed/borderless fullscreen. Collect CPU/GPU frame timings, P95/P99, PresentMon present mode and display timing; actual input-to-photon latency requires appropriate external measurement. Check textures/video/masking, repeated resize/minimize/restore, alt-tab, monitor changes, and a sustained gameplay run. Run Vulkan validation and D3D debug-layer checks. MAILBOX throughput must be compared explicitly before considering this a default.

## Validation performed

`dotnet build g0v0-framework/osu.Framework.Tests/osu.Framework.Tests.csproj -p:UseLocalVeldrid=true --no-restore` succeeded with 0 errors. Log: `migration-audit/low-latency-build.log`. Existing ImageSharp advisory warnings and a Vortice platform warning remain. No visual runtime, driver validation, or input-latency measurement has been performed in this pass. The game executable is not yet rebuilt by this validation command.
