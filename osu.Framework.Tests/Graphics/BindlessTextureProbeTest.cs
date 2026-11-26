// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Configuration;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.IO.Stores;
using osu.Framework.Testing;
using osuTK;
using osuTK.Graphics;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    [NonParallelizable]
    public class BindlessTextureProbeTest
    {
        [Test]
        [Explicit("Requires Vulkan descriptor indexing and GPU rendering.")]
        public void TestDrawWithFullShaderFilenames()
        {
            bool wasOfflineVulkan = FrameworkFeatureFlags.OfflineVulkanRenderer;
            bool wasBindless = FrameworkFeatureFlags.VulkanBindlessTextures;
            var offlineVulkan = typeof(FrameworkFeatureFlags).GetProperty(nameof(FrameworkFeatureFlags.OfflineVulkanRenderer))!;
            var bindless = typeof(FrameworkFeatureFlags).GetProperty(nameof(FrameworkFeatureFlags.VulkanBindlessTextures))!;
            offlineVulkan.SetValue(null, true);
            bindless.SetValue(null, true);

            try
            {
                using var context = new OfflineRenderContext(32, 32);
                var renderer = (VeldridRenderer)context.Renderer;
                if (!renderer.UsesBindlessTextures)
                    Assert.Ignore("The Vulkan device does not support bindless textures.");

                using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
                var shader = shaders.Load("sh_Texture2D.vs", "sh_Texture.fs");

                renderer.BeginFrame(new Vector2(32, 32));
                shader.Bind();
                renderer.DrawQuad(renderer.WhitePixel, new Quad(0, 0, 16, 16), Color4.White);
                shader.Unbind();
                renderer.FinishFrame();
            }
            finally
            {
                bindless.SetValue(null, wasBindless);
                offlineVulkan.SetValue(null, wasOfflineVulkan);
            }
        }

        [Test]
        [Explicit("Creates a real Vulkan device without a window; run only for hardware compatibility verification.")]
        public void TestVulkanDevice()
        {
            if (!GraphicsDevice.IsBackendSupported(GraphicsBackend.Vulkan))
                Assert.Ignore("Vulkan loader unavailable.");
            using var device = GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions { HasMainSwapchain = false });
            using var storage = new TemporaryNativeStorage($"bindless-vulkan-{Guid.NewGuid()}");
            var renderer = new DummyRenderer { GraphicsProbeCache = new GraphicsProbeCache(storage) };
            VeldridBindlessTextureProbe.Run(device, renderer);
            TestContext.Out.WriteLine($"{device.VendorName} / {device.DeviceName}: {renderer.BindlessTextureSupport}");
            Assert.That(renderer.BindlessTextureSupport.Status, Is.Not.EqualTo(BindlessTextureProbeStatus.Failed));
            Assert.That(renderer.BindlessTextureSupport.RendererImplemented, Is.False);
        }
    }
}
