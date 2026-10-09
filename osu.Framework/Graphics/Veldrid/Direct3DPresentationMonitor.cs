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
            nativeSwapchainProperty = swapchain.GetType().GetProperty("DxgiSwapChain", BindingFlags.Instance | BindingFlags.Public);
        }

        public void Update()
        {
            double now = stopwatch.Elapsed.TotalMilliseconds;

            if (now - lastPollTime < poll_interval)
                return;

            lastPollTime = now;
            Direct3DPresentationModel? model = null;
            bool? exclusiveFullscreen = null;

            try
            {
                if (nativeSwapchainProperty?.GetValue(swapchain) is IDXGISwapChain nativeSwapchain)
                {
                    model = nativeSwapchain.Description.SwapEffect switch
                    {
                        SwapEffect.Discard => Direct3DPresentationModel.BitBltDiscard,
                        SwapEffect.Sequential => Direct3DPresentationModel.BitBltSequential,
                        SwapEffect.FlipSequential => Direct3DPresentationModel.FlipSequential,
                        SwapEffect.FlipDiscard => Direct3DPresentationModel.FlipDiscard,
                        _ => null,
                    };

                    var result = nativeSwapchain.GetFullscreenState(out var fullscreen, out var output);
                    // GetFullscreenState returns an owned output reference even though the swap chain is borrowed.
                    output?.Dispose();
                    if (result.Success)
                        exclusiveFullscreen = fullscreen;
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

            var newStatus = new Direct3DPresentationStatus(model, exclusiveFullscreen);

            if (newStatus != Status)
            {
                Volatile.Write(ref status, newStatus);
                Logger.Log($"Direct3D presentation state: {model?.ToString() ?? "Unknown"}, DXGI exclusive fullscreen: {exclusiveFullscreen?.ToString() ?? "Unknown"}");
            }
        }
    }
}
