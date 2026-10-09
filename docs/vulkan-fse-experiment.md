# Local Vulkan full-screen exclusive experiment

This is an opt-in Windows experiment, not a default renderer change.

## Build and run (PowerShell, workspace root)

The sibling `veldrid` checkout must contain the local FSE changes (base commit `a405fe8484`).

```powershell
dotnet build g0v0/osu.Desktop/osu.Desktop.csproj -p:UseLocalVeldrid=true
$env:OSU_GRAPHICS_SURFACE = 'Vulkan'
$env:OSU_GRAPHICS_RENDERER = 'veldrid'
$env:VELDRID_VK_FSE = '1'
& './g0v0/osu.Desktop/bin/Debug/net10.0/g0v0!.exe' --debug-client-id=9081
```

Set `WindowMode = Fullscreen` in the isolated client's `framework.ini` while the application is stopped. Ordinary windowed mode does not acquire FSE. The environment switch is process-local; clear it with `Remove-Item Env:VELDRID_VK_FSE` for ordinary Vulkan. Omit `UseLocalVeldrid=true` and rebuild (including restore) to return to the packaged backend.

## Implementation

- Enable `VK_KHR_get_surface_capabilities2` and `VK_EXT_full_screen_exclusive` only when explicitly requested on Windows.
- Query support and FSE-specific present modes for the Win32 monitor.
- Chain application-controlled exclusive info and Win32 monitor info into swapchain creation.
- Acquire only for a focused, non-minimised window exactly covering its monitor.
- Release when no longer eligible, before replacing a swapchain, and on disposal. Retry failed acquisitions at two-second intervals; handle the exclusive-mode-lost result.
- Print capability/acquire/release results to stdout with `[VK FSE]` prefix. These diagnostics are not automatically written to framework runtime.log.

## Observed on 2026-10-08

RTX 5060 Laptop GPU, NVIDIA 591.91, ordinary Veldrid Vulkan renderer:

- Capability query: `Success, supported=1`.
- `vkAcquireFullScreenExclusiveModeEXT`: `Success (0), acquired=True`.
- Game reached the main menu.
- Minimise/restore exercise produced successful release and reacquisition.
- Closing the first test process produced a successful release.
- Rebuilt with FSE-specific present mode querying and confirmed successful acquisition again.

Logs: `%TEMP%\g0v0-vk-fse\stdout.log` and `final-stdout.log`.

## Limitations

This is a local prototype. No Vulkan validation-layer pass, multi-monitor/hotplug test, long-duration stability test, or latency/presentation telemetry comparison has been completed. Unsupported required extensions fail device creation rather than silently pretending FSE is active. Render-thread polling of window focus is not a replacement for full window-lifecycle integration. Monitor movement without a swapchain rebuild will stop exclusive acquisition until a rebuild occurs. Ordinary fullscreen and successful extension acquisition are distinct; only the acquire result establishes explicit ownership at that time.
