// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// A snapshot of the actual Vulkan swapchain presentation state and mode.
    /// </summary>
    public sealed record VulkanPresentationStatus(
        VulkanPresentMode? PresentMode,
        bool? FullScreenExclusiveSupported,
        bool? ExclusiveFullscreen,
        uint? ImageCount);
}
