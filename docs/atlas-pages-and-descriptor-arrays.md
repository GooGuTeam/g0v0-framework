# Atlas page reuse and Vulkan texture descriptor arrays

## Current implementation

`TextureAtlas` retains the allocation state of older pages for `Guillotiere`, `Etagere`, and `FreeRectangles`. A new allocation first tries the current page, then older live pages in reverse creation order, and creates a page only when none fits. Allocation remains under the atlas retrieval lock. Existing texture coordinates, backing pages, padding, and upload paths are preserved. Explicit `Reset()` forgets all reusable pages and starts a fresh allocation generation.

Older pages are held through weak references. Their textures remain alive through existing regions; the allocator does not retain GPU memory after those regions become unreachable. Dead or unavailable candidates are removed during the next overflow search. This reuses unallocated space, not space occupied by disposed individual regions. The allocators remain append-only within each page.

The latest Classic session log used `Deferred_Vulkan` and reported several 1024-square atlas overflows and standalone textures such as 1644x112 and 2048x1384. Page reuse addresses premature rollover after a large allocation fails. It does not make oversized textures fit or merge draw calls across remaining pages. No reduction in Classic frame time or texture switches has been measured for this change.

## Vulkan descriptor-array foundation

The sibling `g0v0-veldrid` library now supports a fixed sampled-image array at one resource-layout binding:

```csharp
var element = new ResourceLayoutElementDescription("Textures", ResourceKind.TextureReadOnly, ShaderStages.Fragment)
{
    ArrayLength = (uint)views.Length
};
var layout = factory.CreateResourceLayout(new ResourceLayoutDescription(element));
var resources = factory.CreateResourceSet(new ResourceSetDescription(layout, new TextureViewArray(views)));
```

`ArrayLength` zero or one keeps the scalar contract; arrays use a `TextureViewArray` with exactly the declared number of views. All descriptors must be populated. A texture array occupies one `BoundResources` entry; subsequent sampler and buffer bindings keep their original binding numbers. Vulkan descriptor-pool accounting, image writes, sampled-image transitions, and command submission references include every array member. Graphics and compute layout checks include descriptor count. Other backends reject array layouts explicitly.

Array layouts and resource sets use core Vulkan facilities. Non-uniform shader indexing is a separate device feature. `VulkanDeviceOptions.EnableTextureDescriptorIndexing` requests `shaderSampledImageArrayNonUniformIndexing` and `runtimeDescriptorArray` only when both are supported and the Vulkan 1.0 extension dependencies are available. No update-after-bind, partially-bound, or variable-count flags are enabled. `BackendInfoVulkan.TextureDescriptorIndexingEnabled` reports logical-device enablement, and `SampledTextureArrayLimit` reports a conservative limit before accounting for the rest of a pipeline.

The framework requests this optional capability only when `VulkanDescriptorIndexing = true` is set before launch. Probe cache identity includes logical-device enablement and the array limit. Enabling the flag alone does not change drawing or batch boundaries.

Vulkan references: [descriptor indexing extension and dependencies](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_descriptor_indexing.html), [descriptor indexing feature ABI](https://github.com/KhronosGroup/Vulkan-Headers/blob/main/include/vulkan/vulkan_core.h).

## Remaining renderer integration

The framework still submits scalar texture bindings. A usable bindless batch path needs shader reflection and resource-layout array sizes, a per-vertex texture index, a texture table snapshot retained through deferred submission, and indexed RGBA/R8 shaders. Texture uploads, render-target changes, wrap modes, sampler changes, and fallback shaders must keep correct flush boundaries. Descriptor sets in this implementation are immutable after creation, so a table snapshot must be replaced rather than updated while recorded or submitted commands reference it.

Classic skin textures use RGBA shaders. Framework glyph atlases use R8 coverage shaders. Any combined path must preserve that distinction to avoid the white-block regression in legacy skin digits.

GPU rendering/readback, device enablement on hardware, and Classic scene comparisons remain unverified. Compilation and existing CPU atlas regression results are recorded in the task output.
