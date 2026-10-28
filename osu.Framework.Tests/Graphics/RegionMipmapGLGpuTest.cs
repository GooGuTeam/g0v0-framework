// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osuTK;
using GL4 = osuTK.Graphics.OpenGL;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Desktop OpenGL coverage for the GL renderer's regional mipmap path, using a hidden SDL window for the context.
    /// OpenGL ES (Android) uses the same calls but is not exercised here.
    /// </summary>
    [TestFixture]
    public class RegionMipmapGLGpuTest
    {
        private SDL3DesktopWindow window = null!;
        private GLRenderer renderer = null!;
        private bool wasDrawThread;

        [SetUp]
        public void SetUp()
        {
            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            window = new SDL3DesktopWindow(GraphicsSurfaceType.OpenGL, "region-mipmap-test");
            window.Create();

            renderer = new GLRenderer();
            ((IRenderer)renderer).Initialise(((IWindow)window).GraphicsSurface);
        }

        [TearDown]
        public void TearDown()
        {
            MipmapRegions.Enabled = true;
            window.Dispose();
            ThreadSafety.IsDrawThread = wasDrawThread;
        }

        [Test]
        [Explicit("Requires an OpenGL context and GPU readback.")]
        public void TestRegionMipmapsMatchWholeTextureMipmaps()
        {
            const int size = RegionMipmapScenario.SIZE;

            Assert.That(renderer.SupportsRegionMipmaps, Is.True);

            using Texture regional = ((IRenderer)renderer).CreateTexture(size, size);
            using Texture reference = ((IRenderer)renderer).CreateTexture(size, size);

            regional.SetData(RegionMipmapScenario.Upload(RegionMipmapScenario.CreateBase(), new RectangleI(0, 0, size, size)));
            processUploads(regional);

            RegionMipmapScenario.UploadPatches(regional);
            processUploads(regional);

            reference.SetData(RegionMipmapScenario.Upload(RegionMipmapScenario.CreateFinal(), new RectangleI(0, 0, size, size)));
            processUploads(reference);

            Assert.That(((GLTexture)regional.NativeTexture).RegionMipmapGenerations, Is.GreaterThan(0));
            Assert.That(((GLTexture)reference.NativeTexture).RegionMipmapGenerations, Is.Zero);

            RegionMipmapScenario.AssertLevelsEqual(readLevels(regional), readLevels(reference));
        }

        [Test]
        [Explicit("Timing measurement; requires an OpenGL context.")]
        public void TestSmallUploadCostOnLargeTexture([Values(true, false)] bool regionMipmaps)
        {
            const int size = 4096;
            const int frames = 30;
            const int patch = 48;

            using Texture texture = ((IRenderer)renderer).CreateTexture(size, size);
            texture.SetData(RegionMipmapScenario.Upload(new Image<Rgba32>(size, size, new Rgba32(10, 20, 30, 255)), new RectangleI(0, 0, size, size)));
            processUploads(texture);

            MipmapRegions.Enabled = regionMipmaps;

            texture.SetData(RegionMipmapScenario.Upload(new Image<Rgba32>(patch, patch), new RectangleI(100, 100, patch, patch)));
            processUploads(texture);

            var patches = new List<Image<Rgba32>>();
            for (int i = 0; i < frames; i++)
                patches.Add(new Image<Rgba32>(patch, patch, new Rgba32((byte)i, 200, 100, 255)));

            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < frames; i++)
            {
                texture.SetData(RegionMipmapScenario.Upload(patches[i], new RectangleI(100 + i * 120 % 3800, 300 + i * 70 % 3500, patch, patch)));
                processUploads(texture);
            }

            stopwatch.Stop();

            TestContext.Progress.WriteLine($"OpenGL, {size}x{size}, region mipmaps {regionMipmaps}: "
                                           + $"{stopwatch.Elapsed.TotalMilliseconds / frames:F3} ms per frame with one {patch}x{patch} upload (including glFinish)");

            Assert.That(((GLTexture)texture.NativeTexture).RegionMipmapGenerations, regionMipmaps ? Is.GreaterThanOrEqualTo(frames) : Is.Zero);
        }

        private void processUploads(Texture texture)
        {
            renderer.BeginFrame(new Vector2(64));
            ((IRenderer)renderer).BindTexture(texture);
            renderer.FinishFrame();
            GL4.GL.Finish();
        }

        private static List<Rgba32[]> readLevels(Texture texture)
        {
            var native = (GLTexture)texture.NativeTexture;
            var levels = new List<Rgba32[]>();

            GL4.GL.BindTexture(GL4.TextureTarget.Texture2D, native.TextureId);
            GL4.GL.PixelStore(GL4.PixelStoreParameter.PackAlignment, 1);

            for (int level = 0; level <= IRenderer.MAX_MIPMAP_LEVELS; level++)
            {
                int width = native.Width >> level;
                int height = native.Height >> level;
                byte[] bytes = new byte[width * height * 4];

                GL4.GL.GetTexImage(GL4.TextureTarget.Texture2D, level, GL4.PixelFormat.Rgba, GL4.PixelType.UnsignedByte, bytes);
                levels.Add(MemoryMarshal.Cast<byte, Rgba32>(bytes).ToArray());
            }

            return levels;
        }
    }
}
