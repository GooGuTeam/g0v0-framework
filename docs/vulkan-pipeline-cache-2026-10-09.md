# Vulkan device pipeline cache

Local Veldrid now creates one VkPipelineCache per device, uses it for graphics and compute pipeline creation, and destroys it before the device. Host access is serialised with a dedicated lock as required by Vulkan. The cache is in-memory only; no driver-specific cache data is persisted or shared between devices.

This supplements framework pipeline-object reuse: the framework avoids recreating identical pipeline objects, while VkPipelineCache allows the driver to reuse compilation data between different pipeline variants. Expected benefit is reduced pipeline creation work, not necessarily higher steady-state FPS. No measured performance improvement is claimed.

Set `VELDRID_VK_PIPELINE_CACHE=0` before launch to disable cache creation/use for comparison. The default is enabled. Build with `-p:UseLocalVeldrid=true`; default packaged Veldrid is unchanged.

## Validation

Core + synchronization validation, FSE off, RTX 5060 Laptop GPU / NVIDIA 591.91:

- Ordinary Vulkan: `veldrid-20261008-235821`, 19 scenes / 122 logged steps, zero validation errors or warnings, exit 0.
- Deferred Vulkan: `deferred-20261008-235656`, 19 scenes / 122 logged steps, zero validation errors or warnings, exit 0.
- Initial ordinary run `veldrid-20261008-235358` exited 0 after only four scenes. It is incomplete and not counted as a full pass; it was rerun.

Logs: `%TEMP%\g0v0-vulkan-validation\<run>\`.
Framework.Tests and g0v0 Desktop rebuilt successfully. No gameplay performance A/B benchmark or compute-specific workload was run. The selected graphics tests exercise graphics cache usage, not all compute paths. No changes to frames-in-flight, acquire-fence pacing or present semaphore ownership were made in this pass.
