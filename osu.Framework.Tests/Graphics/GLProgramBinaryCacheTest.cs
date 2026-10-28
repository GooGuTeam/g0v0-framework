// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.OpenGL.Shaders;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// A program linked once is restored from its cached driver binary by a later shader instance, and renders the same.
    /// </summary>
    [TestFixture]
    public class GLProgramBinaryCacheTest
    {
        private const int size = 32;

        [Test]
        [Explicit("Requires an OpenGL context.")]
        public void TestProgramRestoredFromBinary()
        {
            string cacheDirectory = Path.Combine(Path.GetTempPath(), $@"g0v0-program-binary-test-{Guid.NewGuid():N}");
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            // A single context; drawing in a second context within the same process is not supported by this harness.
            var window = new SDL3DesktopWindow(GraphicsSurfaceType.OpenGL, "program-binary-test");

            try
            {
                window.Create();

                var renderer = new GLRenderer();
                ((IRenderer)renderer).CacheStorage = new NativeStorage(cacheDirectory);
                ((IRenderer)renderer).Initialise(((IWindow)window).GraphicsSurface);

                Assert.That(renderer.SupportsProgramBinaries, Is.True);

                var compiled = render(renderer, out double compileMs);
                int loadsBefore = GLShader.ProgramBinaryLoads;
                var restored = render(renderer, out double restoreMs);

                TestContext.Progress.WriteLine($"programs restored: {GLShader.ProgramBinaryLoads - loadsBefore}, compile {compileMs:F2} ms, restore {restoreMs:F2} ms");

                Assert.That(GLShader.ProgramBinaryLoads - loadsBefore, Is.EqualTo(1));
                Assert.That(restored, Is.EqualTo(compiled));
                Assert.That(restored, Is.Not.EqualTo(new Color4(0, 0, 0, 255)));
            }
            finally
            {
                window.Dispose();
                ThreadSafety.IsDrawThread = wasDrawThread;

                if (Directory.Exists(cacheDirectory))
                    Directory.Delete(cacheDirectory, true);
            }
        }

        private static Color4 render(GLRenderer renderer, out double shaderInitialisationMs)
        {
            // A separate manager creates a separate program, as a later launch would.
            using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
            var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);

            renderer.BeginFrame(new Vector2(size));

            using var target = renderer.CreateFrameBuffer();
            target.Size = new Vector2(size);
            target.Bind();
            renderer.PushViewport(new RectangleI(0, 0, size, size));
            renderer.Clear(new ClearInfo(Color4.Black));

            var stopwatch = Stopwatch.StartNew();
            shader.Bind();
            shaderInitialisationMs = stopwatch.Elapsed.TotalMilliseconds;

            renderer.DrawQuad(renderer.WhitePixel, new Quad(0, 0, size, size), ColourInfo.SingleColour(new Color4(0.2f, 0.6f, 1, 1)));
            shader.Unbind();
            renderer.PopViewport();
            target.Unbind();
            renderer.FinishFrame();

            using var image = renderer.ExtractFrameBufferData(target);
            var pixel = image!.Pixels[size / 2 * size + size / 2];
            return new Color4(pixel.R, pixel.G, pixel.B, pixel.A);
        }
    }
}
