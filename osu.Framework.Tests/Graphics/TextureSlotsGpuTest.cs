// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Imaging;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osu.Framework.Statistics;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Drawing with texture slots renders the same pixels as binding every texture to unit 0, with fewer texture-change flushes.
    /// The scene alternates five textures (more than the four slots), one of them single-channel, with boxes, overlapping quads and a frame buffer change,
    /// then draws enough textures to fill batches.
    /// </summary>
    [TestFixture]
    public class TextureSlotsGpuTest
    {
        private const int size = 128;
        private const int texture_size = 16;
        private const int cell = 20;

        [Test]
        [Explicit("Requires an offline graphics device and GPU readback.")]
        public void TestVeldridMatchesSingleUnit()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            var renderer = new VeldridRenderer();

            try
            {
                renderer.InitialiseOffline(size, size);
                compare(renderer);
            }
            finally
            {
                if (renderer.IsInitialised)
                    renderer.DisposeOffline();
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [Test]
        [Explicit("Requires an OpenGL context.")]
        public void TestGLMatchesSingleUnit()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            var window = new SDL3DesktopWindow(GraphicsSurfaceType.OpenGL, "texture-slots-test");

            try
            {
                window.Create();

                var renderer = new GLRenderer();
                ((IRenderer)renderer).Initialise(((IWindow)window).GraphicsSurface);
                compare(renderer);
            }
            finally
            {
                window.Dispose();
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        private static void compare(Renderer renderer)
        {
            try
            {
                Renderer.TextureSlotsEnabled = false;

                // On OpenGL, the first render in a new context samples the inner frame buffer differently, with or without slots.
                render(renderer, out _);
                var reference = render(renderer, out int referenceFlushes);

                Renderer.TextureSlotsEnabled = true;
                var slotted = render(renderer, out int slottedFlushes);

                int different = reference.Zip(slotted).Count(p => p.First != p.Second);
                TestContext.Progress.WriteLine($"{renderer.GetType().Name}: texture flushes {referenceFlushes} -> {slottedFlushes}, differing pixels {different}");

                Assert.That(different, Is.Zero);
                Assert.That(slottedFlushes, Is.LessThan(referenceFlushes));
                Assert.That(slotted, Has.Some.Matches<Rgba32>(pixel => pixel.R > 0 || pixel.G > 0 || pixel.B > 0));
            }
            finally
            {
                Renderer.TextureSlotsEnabled = true;
            }
        }

        private static Rgba32[] render(Renderer renderer, out int textureFlushes)
        {
            using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
            var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            var flushes = GlobalStatistics.Get<int>(nameof(FlushBatchSource), nameof(FlushBatchSource.BindTexture));

            renderer.BeginFrame(new Vector2(size));

            // OpenGL frame buffers are created within a frame.
            using var target = renderer.CreateFrameBuffer();
            target.Size = new Vector2(size);
            using var inner = renderer.CreateFrameBuffer();
            inner.Size = new Vector2(texture_size);

            var textures = new[]
            {
                createRgba(renderer, new Color4(1, 0, 0, 1), new Color4(1, 1, 0, 1)),
                createRgba(renderer, new Color4(0, 1, 0, 1), new Color4(0, 1, 1, 1)),
                createRgba(renderer, new Color4(0, 0, 1, 1), new Color4(1, 0, 1, 0.5f)),
                createCoverage(renderer),
                createRgba(renderer, new Color4(1, 1, 1, 0.3f), new Color4(0.5f, 0.2f, 0.1f, 1)),
            };

            renderer.PushDepthInfo(new DepthInfo(false, false));

            // Something to draw from a frame buffer, which then holds a slot while the target is bound.
            inner.Bind();
            renderer.PushViewport(new RectangleI(0, 0, texture_size, texture_size));
            renderer.PushOrtho(new RectangleF(0, 0, texture_size, texture_size));
            renderer.Clear(new ClearInfo(new Color4(0.2f, 0.1f, 0.3f, 1)));
            renderer.SetBlend(BlendingParameters.Mixture);
            shader.Bind();
            renderer.DrawQuad(textures[3], new Quad(2, 2, 12, 12), ColourInfo.SingleColour(Color4.Orange));
            shader.Unbind();
            renderer.PopOrtho();
            renderer.PopViewport();
            inner.Unbind();

            target.Bind();
            renderer.PushViewport(new RectangleI(0, 0, size, size));
            renderer.Clear(new ClearInfo(Color4.Black));
            renderer.SetBlend(BlendingParameters.Mixture);
            shader.Bind();

            for (int i = 0; i < 36; i++)
            {
                var quad = new Quad(i % 6 * cell + 2, i / 6 * cell + 2, cell, cell);
                var colour = ColourInfo.GradientVertical(new Color4(1, 1, 1, 1), new Color4(0.6f + i % 3 * 0.2f, 0.8f, 1, 0.8f));

                if (i % 7 == 6)
                    renderer.DrawQuad(renderer.WhitePixel, quad, ColourInfo.SingleColour(new Color4(0.3f, 0.6f, 0.9f, 0.6f)));
                else if (i % 11 == 10)
                    renderer.DrawFrameBuffer(inner, quad, colour);
                else
                    renderer.DrawQuad(textures[i % textures.Length], quad, colour);
            }

            // Many textures in overlapping quads, so that batches fill up and draw while a quad is being added.
            var many = new Texture[300];

            for (int i = 0; i < many.Length; i++)
            {
                many[i] = createRgba(renderer, new Color4(i % 7 / 7f, 0.5f, 0.2f, 1), Color4.White);
                renderer.DrawQuad(many[i], new Quad(i % 120, 100 + i / 120 * 8, 4, 4), ColourInfo.SingleColour(Color4.White));
            }

            // Overlapping quads, so that draw order between slots shows in blending.
            for (int i = 0; i < 5; i++)
                renderer.DrawQuad(textures[(i * 2) % textures.Length], new Quad(30 + i * 8, 40 + i * 6, 40, 40), ColourInfo.SingleColour(new Color4(1, 1, 1, 0.7f)));

            shader.Unbind();
            renderer.PopViewport();
            target.Unbind();
            renderer.PopDepthInfo();

            textureFlushes = flushes.Value;

            renderer.FinishFrame();
            renderer.WaitUntilIdle();
            using var image = renderer.ExtractFrameBufferData(target);
            Assert.That(image, Is.Not.Null);

            foreach (var texture in textures.Concat(many))
                texture.Dispose();

            return image!.Pixels.ToArray();
        }

        private static Texture createRgba(IRenderer renderer, Color4 from, Color4 to)
        {
            var texture = renderer.CreateTexture(texture_size, texture_size);
            var image = new Image<Rgba32>(texture_size, texture_size);

            for (int y = 0; y < texture_size; y++)
            {
                for (int x = 0; x < texture_size; x++)
                {
                    float t = (x + y) / (2f * (texture_size - 1));
                    image[x, y] = new Rgba32(
                        from.R + (to.R - from.R) * t,
                        from.G + (to.G - from.G) * t,
                        from.B + (to.B - from.B) * t,
                        from.A + (to.A - from.A) * t);
                }
            }

            texture.SetData(new TextureUpload(image));
            return texture;
        }

        private static Texture createCoverage(IRenderer renderer)
        {
            var texture = renderer.CreateTexture(texture_size, texture_size, false, TextureFilteringMode.Linear, WrapMode.None, WrapMode.None, null, TexturePixelFormat.R8Float);
            byte[] coverage = new byte[texture_size * texture_size];

            for (int i = 0; i < coverage.Length; i++)
                coverage[i] = (byte)(i % texture_size * 255 / (texture_size - 1));

            texture.SetData(new AlphaTextureUpload(texture_size, texture_size, coverage));
            return texture;
        }
    }
}
