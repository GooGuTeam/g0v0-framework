// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Queries the Vulkan swapchain on the draw thread and publishes an immutable snapshot for other threads.
    /// </summary>
    internal class VulkanPresentationMonitor
    {
        private const double poll_interval = 500;

        private readonly Swapchain swapchain;
        private readonly PropertyInfo? presentModeProperty;
        private readonly PropertyInfo? fseSupportedProperty;
        private readonly PropertyInfo? fseAcquiredProperty;
        private readonly PropertyInfo? imageCountProperty;

        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private double lastPollTime = -poll_interval;
        private bool errorLogged;

        private VulkanPresentationStatus status = new VulkanPresentationStatus(null, null, null, null);
        public VulkanPresentationStatus Status => Volatile.Read(ref status);

        public VulkanPresentationMonitor(Swapchain swapchain)
        {
            this.swapchain = swapchain;

            Type swapchainType = swapchain.GetType();
            presentModeProperty = swapchainType.GetProperty("CurrentPresentMode", BindingFlags.Instance | BindingFlags.Public);
            fseSupportedProperty = swapchainType.GetProperty("FullScreenExclusiveSupported", BindingFlags.Instance | BindingFlags.Public);
            fseAcquiredProperty = swapchainType.GetProperty("FullScreenExclusiveAcquired", BindingFlags.Instance | BindingFlags.Public);
            imageCountProperty = swapchainType.GetProperty("ImageCount", BindingFlags.Instance | BindingFlags.Public);
        }

        public void Update()
        {
            double now = stopwatch.Elapsed.TotalMilliseconds;

            if (now - lastPollTime < poll_interval)
                return;

            lastPollTime = now;
            VulkanPresentMode? mode = null;
            bool? fseSupported = null;
            bool? exclusiveFullscreen = null;
            uint? imageCount = null;

            try
            {
                if (presentModeProperty != null)
                {
                    object? modeVal = presentModeProperty.GetValue(swapchain);
                    if (modeVal != null)
                    {
                        string modeStr = modeVal.ToString() ?? string.Empty;
                        if (modeStr.Contains("Immediate", StringComparison.OrdinalIgnoreCase))
                            mode = VulkanPresentMode.Immediate;
                        else if (modeStr.Contains("Mailbox", StringComparison.OrdinalIgnoreCase))
                            mode = VulkanPresentMode.Mailbox;
                        else if (modeStr.Contains("FifoRelaxed", StringComparison.OrdinalIgnoreCase))
                            mode = VulkanPresentMode.FifoRelaxed;
                        else if (modeStr.Contains("Fifo", StringComparison.OrdinalIgnoreCase))
                            mode = VulkanPresentMode.Fifo;
                    }

                    if (fseSupportedProperty?.GetValue(swapchain) is bool supported)
                        fseSupported = supported;

                    if (fseAcquiredProperty?.GetValue(swapchain) is bool acquired)
                        exclusiveFullscreen = acquired;

                    if (imageCountProperty?.GetValue(swapchain) is uint count)
                        imageCount = count;
                }
                else if (!errorLogged)
                {
                    Logger.Log("Vulkan presentation diagnostics unavailable: swapchain presentation properties missing.", level: LogLevel.Important);
                    errorLogged = true;
                }
            }
            catch (Exception ex)
            {
                if (!errorLogged)
                {
                    Logger.Error(ex, "Failed to query Vulkan presentation state.");
                    errorLogged = true;
                }
            }

            var newStatus = new VulkanPresentationStatus(mode, fseSupported, exclusiveFullscreen, imageCount);

            if (newStatus != Status)
            {
                Volatile.Write(ref status, newStatus);
                Logger.Log($"Vulkan presentation state: {mode?.ToString() ?? "Unknown"}, exclusive fullscreen: {exclusiveFullscreen?.ToString() ?? "Unknown"}, image count: {imageCount?.ToString() ?? "Unknown"}");
            }
        }
    }
}
