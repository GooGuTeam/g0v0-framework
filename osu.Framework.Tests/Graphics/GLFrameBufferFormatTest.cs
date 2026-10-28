// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Framebuffers of non-RGBA formats must initialise and resize on the GL renderer (used on Android).
    /// </summary>
    [TestFixture]
    public class GLFrameBufferFormatTest
    {
        [Test]
        [Explicit("Requires an OpenGL context.")]
        public void TestSingleChannelFrameBufferCreatesAndResizes([Values(TexturePixelFormat.R8Float, TexturePixelFormat.R8G8B8A8Float)] TexturePixelFormat format)
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var window = new SDL3DesktopWindow(GraphicsSurfaceType.OpenGL, "framebuffer-format-test");

            try
            {
                window.Create();

                var renderer = new GLRenderer();
                ((IRenderer)renderer).Initialise(((IWindow)window).GraphicsSurface);

                renderer.BeginFrame(new Vector2(64));

                using (var frameBuffer = renderer.CreateFrameBuffer(format))
                {
                    frameBuffer.Size = new Vector2(32, 16);
                    frameBuffer.Size = new Vector2(48, 24);
                    Assert.That(frameBuffer.Texture.Width, Is.EqualTo(48));
                }

                renderer.FinishFrame();
            }
            finally
            {
                window.Dispose();
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }
    }
}
