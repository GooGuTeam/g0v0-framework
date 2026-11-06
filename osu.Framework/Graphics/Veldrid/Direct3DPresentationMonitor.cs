// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using Veldrid;
using Vortice.DXGI;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Queries DXGI on the draw thread and publishes an immutable snapshot for other threads.
    /// </summary>
    internal class Direct3DPresentationMonitor
    {
        private const double poll_interval = 500;

        private readonly Swapchain swapchain;
        private readonly PropertyInfo? nativeSwapchainProperty;
        private readonly PropertyInfo? frameLatencyWaitProperty;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private double lastPollTime = -poll_interval;
        private bool errorLogged;

        private Direct3DPresentationStatus status = new Direct3DPresentationStatus(null, null);
        public Direct3DPresentationStatus Status => Volatile.Read(ref status);

        public Direct3DPresentationMonitor(Swapchain swapchain)
        {
            this.swapchain = swapchain;
            // The pinned Veldrid backend exposes this property on its internal D3D11Swapchain type,
            // but BackendInfoD3D11 does not provide a public swap chain accessor. Never retain or dispose
            // the borrowed native wrapper: Veldrid can replace it when tearing settings change.
            nativeSwapchainProperty = swapchain.GetType().GetProperty(@"DxgiSwapChain", BindingFlags.Instance | BindingFlags.Public);
            frameLatencyWaitProperty = swapchain.GetType().GetProperty(@"FrameLatencyWaitActive", BindingFlags.Instance | BindingFlags.Public);
        }

        public void Update()
        {
            double now = stopwatch.Elapsed.TotalMilliseconds;

            if (now - lastPollTime < poll_interval)
                return;

            lastPollTime = now;
            Direct3DPresentationModel? model = null;
            bool? exclusiveFullscreen = null;
            bool? waitActive = null;
            int? maximumLatency = null;
            bool? hardwareOverlay = null;
            bool? composed = null;

            try
            {
                if (nativeSwapchainProperty?.GetValue(swapchain) is IDXGISwapChain nativeSwapchain)
                {
                    var result = nativeSwapchain.GetFullscreenState(out var fullscreen, out var output);
                    // The output reference is owned even though the swapchain is borrowed.
                    output?.Dispose();
                    if (result.Success)
                        exclusiveFullscreen = fullscreen;

                    var description = nativeSwapchain.Description;
                    model = description.SwapEffect switch
                    {
                        SwapEffect.Discard => Direct3DPresentationModel.BitBltDiscard,
                        SwapEffect.Sequential => Direct3DPresentationModel.BitBltSequential,
                        SwapEffect.FlipSequential => Direct3DPresentationModel.FlipSequential,
                        SwapEffect.FlipDiscard => Direct3DPresentationModel.FlipDiscard,
                        _ => null,
                    };

                    waitActive = frameLatencyWaitProperty?.GetValue(swapchain) as bool?;
                    // GetMaximumFrameLatency is only valid for waitable swapchains.
                    // D3D11 exclusive ownership uses a non-waitable swapchain.
                    if ((description.Flags & SwapChainFlags.FrameLatencyWaitableObject) != 0)
                    {
                        try
                        {
                            using var swapchain2 = nativeSwapchain.QueryInterfaceOrNull<IDXGISwapChain2>();
                            if (swapchain2 != null)
                                maximumLatency = swapchain2.MaximumFrameLatency;
                        }
                        catch (Exception)
                        {
                            // Optional pacing information must not invalidate fullscreen status.
                        }
                    }

                    // Media statistics may be unsupported or disjoint during a mode change.
                    // Failure must not discard the independently queried swapchain properties.
                    if (exclusiveFullscreen != true)
                    {
                        try
                        {
                            using var media = nativeSwapchain.QueryInterfaceOrNull<IDXGISwapChainMedia>();
                            if (media != null)
                            {
                                var statistics = media.FrameStatisticsMedia;
                                if (statistics.PresentCount != 0)
                                {
                                    // DXGI_FRAME_PRESENTATION_MODE: Composed = 0, Overlay = 1, None = 2.
                                    composed = statistics.CompositionMode == 0;
                                    hardwareOverlay = (int)statistics.CompositionMode == 1;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Unavailable statistics leave the compositor path unknown.
                        }
                    }
                }
                else if (!errorLogged)
                {
                    Logger.Log("Direct3D presentation diagnostics unavailable: no native DXGI swap chain accessor.", level: LogLevel.Important);
                    errorLogged = true;
                }
            }
            catch (Exception ex)
            {
                if (!errorLogged)
                {
                    Logger.Error(ex, "Failed to query Direct3D presentation state.");
                    errorLogged = true;
                }
            }

            var newStatus = new Direct3DPresentationStatus(model, exclusiveFullscreen)
            {
                FrameLatencyWaitActive = waitActive,
                MaximumFrameLatency = maximumLatency,
                HardwareOverlay = hardwareOverlay,
                Composed = composed,
            };

            if (newStatus != Status)
            {
                Volatile.Write(ref status, newStatus);
                Logger.Log($"Direct3D presentation state: {model?.ToString() ?? "Unknown"}, DXGI exclusive fullscreen: {exclusiveFullscreen?.ToString() ?? "Unknown"}");
            }
        }
    }
}
