// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    internal static class VeldridTextureSizeProbe
    {
        public static void Run(GraphicsDevice device, Renderer renderer)
        {
            try
            {
                renderer.ProbeTextureSizes($"{device.BackendType}|{device.VendorName}|{device.DeviceName}|{device.ApiVersion}", (size, format) => tryAllocate(device, size, format));
            }
            catch (Exception e)
            {
                // Optional diagnostics must not turn successful device initialisation into a failure.
                Logger.Log($"Texture size probe failed: {e.Message}");
            }
        }

        /// <summary>
        /// Allocates a render target of the given size and clears it, so that drivers which allocate lazily commit the memory.
        /// </summary>
        private static bool tryAllocate(GraphicsDevice device, int size, TexturePixelFormat format)
        {
            var factory = device.ResourceFactory;
            var pixelFormat = format == TexturePixelFormat.R8Float ? global::Veldrid.PixelFormat.R8UNorm : global::Veldrid.PixelFormat.R8G8B8A8UNorm;

            global::Veldrid.Texture? texture = null;
            Framebuffer? framebuffer = null;
            CommandList? commands = null;

            try
            {
                texture = factory.CreateTexture(TextureDescription.Texture2D((uint)size, (uint)size, 1, 1, pixelFormat, TextureUsage.RenderTarget | TextureUsage.Sampled));
                framebuffer = factory.CreateFramebuffer(new FramebufferDescription(null, texture));
                commands = factory.CreateCommandList();

                commands.Begin();
                commands.SetFramebuffer(framebuffer);
                commands.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 0));
                commands.End();

                device.SubmitCommands(commands);
                device.WaitForIdle();
                return true;
            }
            catch (Exception e)
            {
                Logger.Log($"Texture size probe: {size}x{size} {format} failed: {e.Message}");
                return false;
            }
            finally
            {
                commands?.Dispose();
                framebuffer?.Dispose();
                texture?.Dispose();
            }
        }
    }
}
