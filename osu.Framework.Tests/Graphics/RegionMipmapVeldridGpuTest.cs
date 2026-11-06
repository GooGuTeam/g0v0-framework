// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.Imaging;
using osuTK;
using Veldrid;
using Texture = osu.Framework.Graphics.Textures.Texture;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class RegionMipmapVeldridGpuTest
    {
        private VeldridRenderer renderer = null!;
        private bool wasDrawThread;

        [SetUp]
        public void SetUp()
        {
            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            renderer = new VeldridRenderer();
            renderer.InitialiseOffline(64, 64);
        }

        [TearDown]
        public void TearDown()
        {
            MipmapRegions.Enabled = true;

            if (renderer.IsInitialised)
                renderer.DisposeOffline();
            ThreadSafety.IsDrawThread = wasDrawThread;
        }

        [Test]
        [Explicit("Requires an offline graphics device and GPU readback.")]
        public void TestRegionMipmapsMatchWholeTextureMipmaps()
        {
            const int size = RegionMipmapScenario.SIZE;

            using Texture regional = ((IRenderer)renderer).CreateTexture(size, size);
            using Texture reference = ((IRenderer)renderer).CreateTexture(size, size);

            regional.SetData(RegionMipmapScenario.Upload(RegionMipmapScenario.CreateBase(), new RectangleI(0, 0, size, size)));
            processUploads(regional);

            RegionMipmapScenario.UploadPatches(regional);
            processUploads(regional);

            reference.SetData(RegionMipmapScenario.Upload(RegionMipmapScenario.CreateFinal(), new RectangleI(0, 0, size, size)));
            processUploads(reference);

            Assert.That(((VeldridTexture)regional.NativeTexture).RegionMipmapGenerations, Is.GreaterThan(0));
            Assert.That(((VeldridTexture)reference.NativeTexture).RegionMipmapGenerations, Is.Zero);

            RegionMipmapScenario.AssertLevelsEqual(readLevels(regional), readLevels(reference));
        }

        [Test]
        [Explicit("Timing measurement; requires an offline graphics device.")]
        public void TestSmallUploadCostOnLargeTexture([Values(true, false)] bool regionMipmaps)
        {
            const int size = 4096;
            const int frames = 30;
            const int patch = 48;

            using Texture texture = ((IRenderer)renderer).CreateTexture(size, size);
            texture.SetData(RegionMipmapScenario.Upload(new Image<Rgba32>(size, size, new Rgba32(10, 20, 30, 255)), new RectangleI(0, 0, size, size)));
            processUploads(texture);

            MipmapRegions.Enabled = regionMipmaps;

            var patches = new List<Image<Rgba32>>();
            for (int i = 0; i < frames; i++)
                patches.Add(new Image<Rgba32>(patch, patch, new Rgba32((byte)i, 200, 100, 255)));

            // Warm up both paths' resources before timing.
            // Uploads dispose their images, so the warm-up uses its own.
            texture.SetData(RegionMipmapScenario.Upload(new Image<Rgba32>(patch, patch), new RectangleI(100, 100, patch, patch)));
            processUploads(texture);

            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < frames; i++)
            {
                texture.SetData(RegionMipmapScenario.Upload(patches[i], new RectangleI(100 + i * 120 % 3800, 300 + i * 70 % 3500, patch, patch)));
                processUploads(texture);
            }

            stopwatch.Stop();

            TestContext.Progress.WriteLine($"{renderer.Device.BackendType}, {size}x{size}, region mipmaps {regionMipmaps}: "
                                           + $"{stopwatch.Elapsed.TotalMilliseconds / frames:F3} ms per frame with one {patch}x{patch} upload (including GPU completion)");

            Assert.That(((VeldridTexture)texture.NativeTexture).RegionMipmapGenerations, regionMipmaps ? Is.GreaterThanOrEqualTo(frames) : Is.Zero);
        }

        private void processUploads(Texture texture)
        {
            renderer.BeginFrame(new Vector2(64));
            ((IRenderer)renderer).BindTexture(texture);
            renderer.FinishFrame();
            renderer.WaitUntilIdle();
        }

        private List<Rgba32[]> readLevels(Texture texture)
        {
            var native = ((VeldridTexture)texture.NativeTexture).GetResourceList()[0].Texture;
            var device = renderer.Device;
            var factory = device.ResourceFactory;

            using var staging = factory.CreateTexture(TextureDescription.Texture2D(native.Width, native.Height, IRenderer.MAX_MIPMAP_LEVELS + 1, 1, native.Format, TextureUsage.Staging));
            using var commands = factory.CreateCommandList();

            commands.Begin();

            for (uint level = 0; level <= IRenderer.MAX_MIPMAP_LEVELS; level++)
                commands.CopyTexture(native, 0, 0, 0, level, 0, staging, 0, 0, 0, level, 0, native.Width >> (int)level, native.Height >> (int)level, 1, 1);

            commands.End();
            device.SubmitCommands(commands);
            device.WaitForIdle();

            var levels = new List<Rgba32[]>();

            for (uint level = 0; level <= IRenderer.MAX_MIPMAP_LEVELS; level++)
            {
                int width = (int)native.Width >> (int)level;
                int height = (int)native.Height >> (int)level;
                byte[] bytes = new byte[width * height * 4];
                var mapped = device.Map(staging, MapMode.Read, level);

                try
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(mapped.Data + y * (int)mapped.RowPitch, bytes, y * width * 4, width * 4);
                }
                finally
                {
                    device.Unmap(staging, level);
                }

                levels.Add(MemoryMarshal.Cast<byte, Rgba32>(bytes).ToArray());
            }

            return levels;
        }
    }
}
