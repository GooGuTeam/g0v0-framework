// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Deferred;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Imaging;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Platform.SDL3;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics.Deferred
{
    [TestFixture]
    public class DeferredStorageBufferGpuTest
    {
        private const int size = 256;

        [Test]
        [Explicit("Requires a Direct3D 11 window and GPU readback.")]
        public void TestMasksMatchImmediateAcrossBuffersAndFrames()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            bool wasPerVertexMasking = PerVertexMasking.Enabled;
            ThreadSafety.IsDrawThread = true;
            PerVertexMasking.Enabled = true;

            try
            {
                var reference = capture(new VeldridRenderer());
                var actual = capture(new DeferredRenderer());

                for (int frame = 0; frame < reference.Length; frame++)
                {
                    Assert.That(actual[frame], Is.EqualTo(reference[frame]), $"frame {frame}");
                    Assert.That(actual[frame][4 * size + 4].R, Is.GreaterThan(0), "The first rounded cell must render.");
                }
            }
            finally
            {
                ThreadSafety.IsDrawThread = wasDrawThread;
                PerVertexMasking.Enabled = wasPerVertexMasking;
            }
        }

        private static Rgba32[][] capture(Renderer renderer)
        {
            using var window = new SDL3DesktopWindow(GraphicsSurfaceType.Direct3D11, "deferred-storage-test");
            window.Create();
            ThreadSafety.IsDrawThread = false;
            ((IRenderer)renderer).Initialise(((IWindow)window).GraphicsSurface);
            ThreadSafety.IsDrawThread = true;
            using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
            var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            var result = new Rgba32[3][];

            try
            {
                for (int frame = 0; frame < result.Length; frame++)
                {
                    renderer.BeginFrame(new Vector2(size));
                    renderer.PushDepthInfo(new DepthInfo(false, false));
                    renderer.Clear(new ClearInfo(Color4.Black));
                    renderer.SetBlend(BlendingParameters.Mixture);
                    shader.Bind();

                    // More than 4096 distinct masks crosses the storage-buffer boundary. On the next frame
                    // change only widely separated entries, retaining all intervening palette and mask bytes.
                    for (int i = 0; i < 4225; i++)
                    {
                        int x = 3 + i % 65 * 3;
                        int y = 3 + i / 65 * 3;
                        bool changed = frame == 1 && (i == 0 || i == 4090 || i == 4224);
                        renderer.PushMaskingInfo(new MaskingInfo
                        {
                            ScreenSpaceAABB = new RectangleI(x, y, 3, 3),
                            MaskingRect = new RectangleF(x, y, 3, 3),
                            ToMaskingSpace = Matrix3.Identity,
                            CornerRadius = changed ? 1.5f : 0.5f + i % 3 * 0.25f,
                            CornerExponent = 2,
                            BlendRange = 1,
                            AlphaExponent = 1,
                        });
                        renderer.DrawQuad(renderer.WhitePixel, new Quad(x, y, 3, 3), ColourInfo.SingleColour(new Color4(1f, 0.4f, 0.2f, 0.7f)));
                        renderer.PopMaskingInfo();
                    }

                    shader.Unbind();
                    renderer.PopDepthInfo();
                    renderer.FinishFrame();
                    renderer.WaitUntilIdle();
                    using var image = renderer.TakeScreenshot();
                    result[frame] = image.Pixels.ToArray();
                }
            }
            finally
            {
                ((IVeldridRenderer)renderer).Device.Dispose();
            }

            return result;
        }
    }
}
