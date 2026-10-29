// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Configuration;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.Imaging;
using osu.Framework.IO.Stores;
using osuTK;
using osuTK.Graphics;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    [NonParallelizable]
    [Explicit("Requires a Vulkan graphics device and GPU readback.")]
    public class VulkanTextureUploadGpuTest
    {
        [TestCase(false, false, 0)]
        [TestCase(true, false, 0)]
        [TestCase(false, false, 1)]
        [TestCase(true, false, 1)]
        [TestCase(false, true, 1)]
        [TestCase(true, true, 1)]
        public void TestUploadsPreserveDrawOrder(bool spanUpload, bool regionalMipmaps, int mipLevel)
        {
            const int texture_size = 64;
            const int cell_size = 32;
            const int width = cell_size * 3;
            var colours = new[] { new Rgba32(255, 0, 0, 255), new Rgba32(0, 0, 255, 255), new Rgba32(0, 255, 0, 255) };

            bool wasDrawThread = ThreadSafety.IsDrawThread;
            bool wasOfflineVulkan = FrameworkFeatureFlags.OfflineVulkanRenderer;
            var offlineVulkan = typeof(FrameworkFeatureFlags).GetProperty(nameof(FrameworkFeatureFlags.OfflineVulkanRenderer))!;
            offlineVulkan.SetValue(null, true);
            ThreadSafety.IsDrawThread = true;
            var renderer = new VeldridRenderer();

            try
            {
                renderer.InitialiseOffline(width, cell_size);
                Assert.That(renderer.Device.BackendType, Is.EqualTo(GraphicsBackend.Vulkan));

                using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
                var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
                using var texture = ((IRenderer)renderer).CreateTexture(texture_size, texture_size);
                texture.BypassTextureUploadQueueing = true;
                texture.MipLevel = mipLevel;
                var native = (VeldridTexture)texture.NativeTexture;
                using var target = renderer.CreateFrameBuffer();
                target.Size = new Vector2(width, cell_size);

                renderer.BeginFrame(new Vector2(width, cell_size));
                renderer.PushDepthInfo(new DepthInfo(false, false));
                target.Bind();
                renderer.PushViewport(new RectangleI(0, 0, width, cell_size));
                renderer.PushOrtho(new RectangleF(0, 0, width, cell_size));
                renderer.Clear(new ClearInfo(Color4.Black));
                renderer.SetBlend(BlendingParameters.None);
                shader.Bind();

                // The first draw reads the original texture. Later uploads replace only its left half.
                texture.SetData(new TextureUpload(new Image<Rgba32>(texture_size, texture_size, colours[0])));
                native.Upload();

                for (int i = 0; i < colours.Length; i++)
                {
                    if (i > 0)
                    {
                        var pixels = new Rgba32[texture_size / 2 * texture_size];
                        Array.Fill(pixels, colours[i]);
                        var resource = native.GetResourceList()[0].Texture;

                        if (spanUpload)
                            renderer.UpdateTexture<Rgba32>(resource, 0, 0, texture_size / 2, texture_size, 0, pixels);
                        else
                        {
                            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                            try
                            {
                                renderer.UpdateTexture(resource, 0, 0, texture_size / 2, texture_size, 0, handle.AddrOfPinnedObject(), texture_size / 2 * 4);
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }

                        if (mipLevel > 0)
                        {
                            if (regionalMipmaps)
                                renderer.GenerateMipmaps(native, new RectangleI(0, 0, texture_size / 2, texture_size));
                            else
                                renderer.GenerateMipmaps(native);
                        }
                    }

                    renderer.DrawQuad(texture, new Quad(i * cell_size, 0, cell_size, cell_size), ColourInfo.SingleColour(Color4.White));
                    // Record the draw before the next upload, without submitting or waiting for the GPU.
                    renderer.FlushCurrentBatch(null);
                }

                shader.Unbind();
                renderer.PopOrtho();
                renderer.PopViewport();
                target.Unbind();
                renderer.PopDepthInfo();
                renderer.FinishFrame();
                renderer.WaitUntilIdle();

                using var image = renderer.ExtractFrameBufferData(target);
                Assert.That(image, Is.Not.Null);

                for (int i = 0; i < colours.Length; i++)
                {
                    Assert.That(image![i * cell_size + cell_size / 4, cell_size / 2], Is.EqualTo(colours[i]), $"Draw {i}: uploaded half");
                    Assert.That(image[i * cell_size + cell_size * 3 / 4, cell_size / 2], Is.EqualTo(colours[0]), $"Draw {i}: untouched half");
                }
            }
            finally
            {
                try
                {
                    if (renderer.IsInitialised)
                        renderer.DisposeOffline();
                }
                finally
                {
                    ThreadSafety.IsDrawThread = wasDrawThread;
                    offlineVulkan.SetValue(null, wasOfflineVulkan);
                }
            }
        }
    }
}
