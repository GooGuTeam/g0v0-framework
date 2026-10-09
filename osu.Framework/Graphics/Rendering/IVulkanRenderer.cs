// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Exposes presentation diagnostics for renderers which can use a Vulkan backend.
    /// </summary>
    public interface IVulkanRenderer
    {
        /// <summary>
        /// The latest Vulkan swapchain presentation state, refreshed on the draw thread.
        /// Null if the active backend is not Vulkan.
        /// </summary>
        VulkanPresentationStatus? PresentationStatus { get; }
    }
}
