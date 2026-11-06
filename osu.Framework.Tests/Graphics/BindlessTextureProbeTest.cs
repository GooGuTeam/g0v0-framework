// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Testing;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class BindlessTextureProbeTest
    {
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
