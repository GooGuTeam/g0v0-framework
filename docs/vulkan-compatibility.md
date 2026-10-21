# Vulkan compatibility hardening

These changes apply to the sibling Veldrid backend used by both ordinary and deferred Vulkan renderers. They do not switch to another graphics API on failure.

## Changes

- Device creation emits one queue request per distinct graphics/presentation family using that family's index. Headless devices reuse the graphics family as the present-family placeholder. Missing usable families produce a specific error; surface-support query failures are checked.
- Surface selection accepts RGBA sRGB when BGRA sRGB is unavailable, preserving the requested colour space. Empty format/mode lists are rejected explicitly.
- Swapchains use a mandated current extent where specified, a supported surface transform and a supported composite-alpha mode. Transfer-source capture usage remains optional. Colour-attachment and transfer-destination usage remain mandatory because the current initialisation path clears images using transfer commands.
- Initial fence creation/results are checked. If the first acquisition recreates the swapchain and consumes its fence, startup no longer waits a second time on that reset fence. Zero drawable extents at initial creation are rejected explicitly.
- Missing FSE entry points disable that optional path. FSE mode enumeration retries incomplete results up to three times; errors or empty results leave ordinary Vulkan presentation active. FSE capability state reflects that fallback.

## Follow-up: adapter selection and enumeration

- Physical-device selection preserves driver enumeration order but skips adapters missing explicitly required device extensions, swapchain support for a windowed surface, or usable graphics/presentation queues. This is a suitability filter, not a fastest-GPU ranking or a manual GPU selector.
- A combined graphics/presentation queue family is preferred when available; split families remain supported. Offscreen devices require only a graphics family.
- Physical-device, extension, surface-format and base present-mode enumeration retries `VK_INCOMPLETE` up to three times and trims arrays to returned counts. Persistent instability produces a specific error rather than an unbounded loop.
- Existing user logs `1792247190.runtime.log` and `1792247589.runtime.log` show Deferred Vulkan initialising on an NVIDIA GeForce RTX 5060 Laptop GPU and reporting acquired FSE. These logs predate this follow-up and do not validate its new branches.

## Validation and limits

A user-requested Vulkan validation run enabled Khronos Validation Layers 1.4.363.0 Core and Synchronization checks on an NVIDIA GeForce RTX 5060 Laptop GPU. After removing the conditional Release result-check helper, initialisation and first-run UI rendering produced no validation errors or warnings at the observed checkpoint. This does not cover gameplay, shutdown, different drivers, split queues, Linux/Wayland or Windows FSE transitions. No unit tests or streaming connections were run.

Device selection does not yet check every later resource/format requirement or retry another GPU after logical-device creation fails. Mandatory surface loss/device loss is not automatically recovered. Repeated out-of-date results during acquisition are bounded to three recreation attempts; zero-extent resume handling and initialisation-failure resource cleanup need further work. Swapchain-creation failures are propagated rather than attempting unsafe retries with a retired old swapchain. The existing native recording capability gates and explicit opt-in remain unchanged.

Framework now references `g0v0.Veldrid` and `g0v0.Veldrid.SPIRV` version `4.9.0-g0v0.2`. The paired SPIRV package prevents an upstream `ppy.Veldrid` runtime dependency from replacing the fork DLL. Local Veldrid project-reference switches and compile-time capture fallbacks have been removed.

The package integration build restores the four renderer packages from a temporary local feed; client framework/resources references remain temporary and are restored after building. The final client dependency manifest contains no `ppy.Veldrid*` packages, and renderer/SPIRV/native recording output hashes match their sources without manual DLL replacement. The new packages must be published before a clean machine can restore the framework from NuGet.org; the local build does not prove public availability.
