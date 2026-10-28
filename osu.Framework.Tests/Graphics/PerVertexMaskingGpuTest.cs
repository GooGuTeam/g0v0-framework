// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
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
    /// Per-vertex masking renders the same pixels as global masking uniforms (up to one rounding step at anti-aliased edges), while masking changes no longer split batches.
    /// The scene covers rotated and nested masks, a mask wider than its parent's scissor, borders, a hollow edge effect,
    /// depth writes (which fall back to hardware clipping), disabled scissor, and drawing into an offset frame buffer.
    /// </summary>
    [TestFixture]
    public class PerVertexMaskingGpuTest
    {
        private const int size = 128;

        [Test]
        [Explicit("Requires an offline graphics device and GPU readback.")]
        public void TestVeldridMatchesGlobalUniforms()
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
        public void TestGLMatchesGlobalUniforms()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            var window = new SDL3DesktopWindow(GraphicsSurfaceType.OpenGL, "per-vertex-masking-test");

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
            bool wasEnabled = PerVertexMasking.Enabled;

            try
            {
                PerVertexMasking.Enabled = false;
                var reference = render(renderer, out int referenceFlushes);

                PerVertexMasking.Enabled = true;
                var perVertex = render(renderer, out int perVertexFlushes);

                int different = reference.Zip(perVertex).Count(p => p.First != p.Second);
                TestContext.Progress.WriteLine($"{renderer.GetType().Name}: masking and scissor flushes {referenceFlushes} -> {perVertexFlushes}, differing pixels {different}");

                for (int i = 0; i < reference.Length; i++)
                {
                    if (reference[i] != perVertex[i])
                        TestContext.Progress.WriteLine($"  ({i % size}, {i / size}): {reference[i].R},{reference[i].G},{reference[i].B},{reference[i].A} -> {perVertex[i].R},{perVertex[i].G},{perVertex[i].B},{perVertex[i].A}");
                }

                // The two paths compile to different shader code, so anti-aliased edges may round differently by one step.
                Assert.That(reference.Zip(perVertex).All(p => maxChannelDifference(p.First, p.Second) <= 1), Is.True);
                Assert.That(different, Is.LessThanOrEqualTo(8));
                Assert.That(perVertexFlushes, Is.LessThan(referenceFlushes));
                Assert.That(perVertex, Has.Some.Matches<Rgba32>(pixel => pixel.R > 0 || pixel.G > 0 || pixel.B > 0));
            }
            finally
            {
                PerVertexMasking.Enabled = wasEnabled;
            }
        }

        private static Rgba32[] render(Renderer renderer, out int clippingFlushes)
        {
            // A new manager compiles the shader again, under the current opt-in setting.
            using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
            var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            Assert.That(((IPerVertexMaskingShader)shader).UsesPerVertexMasking, Is.EqualTo(PerVertexMasking.Enabled));

            var maskingFlushes = GlobalStatistics.Get<int>(nameof(FlushBatchSource), nameof(FlushBatchSource.SetMasking));
            var scissorFlushes = GlobalStatistics.Get<int>(nameof(FlushBatchSource), nameof(FlushBatchSource.SetScissor));

            renderer.BeginFrame(new Vector2(size));

            // OpenGL frame buffers are created within a frame.
            using var target = renderer.CreateFrameBuffer();
            target.Size = new Vector2(size);
            using var inner = renderer.CreateFrameBuffer();
            inner.Size = new Vector2(48);

            // An offset frame buffer, set up as BufferedDrawNode does, with its own masks.
            var innerArea = new RectangleF(70, 60, 48, 48);
            inner.Bind();
            renderer.PushMaskingInfo(new MaskingInfo
            {
                ScreenSpaceAABB = new RectangleI(70, 60, 49, 49),
                MaskingRect = innerArea,
                ToMaskingSpace = Matrix3.Identity,
                BlendRange = 1,
                AlphaExponent = 1,
            }, true);
            renderer.PushViewport(new RectangleI(0, 0, 48, 48));
            renderer.PushScissor(new RectangleI(0, 0, 48, 48));
            renderer.PushScissorOffset(new Vector2I(70, 60));
            renderer.PushOrtho(innerArea);
            renderer.PushDepthInfo(new DepthInfo(false, false));
            renderer.Clear(new ClearInfo(new Color4(0, 0, 0.2f, 1)));
            renderer.SetBlend(BlendingParameters.Mixture);
            shader.Bind();

            renderer.PushMaskingInfo(mask(new RectangleF(74, 64, 30, 36), Matrix3.Identity, 8, 2));
            draw(renderer, new Quad(66, 58, 60, 50), new Color4(0.9f, 0.4f, 0.1f, 0.8f));
            renderer.PushMaskingInfo(mask(new RectangleF(80, 70, 40, 20), Matrix3.Identity, 4, 0));
            draw(renderer, new Quad(76, 66, 50, 30), new Color4(0.1f, 0.8f, 0.9f, 0.7f));
            renderer.PopMaskingInfo();
            renderer.PopMaskingInfo();

            shader.Unbind();
            renderer.PopDepthInfo();
            renderer.PopOrtho();
            renderer.PopScissorOffset();
            renderer.PopScissor();
            renderer.PopViewport();
            renderer.PopMaskingInfo();
            inner.Unbind();

            target.Bind();
            renderer.PushViewport(new RectangleI(0, 0, size, size));
            renderer.PushDepthInfo(new DepthInfo(false, false));
            renderer.Clear(new ClearInfo(Color4.Black));
            renderer.SetBlend(BlendingParameters.Mixture);
            shader.Bind();

            draw(renderer, new Quad(0, 0, size, size), new Color4(0.1f, 0.1f, 0.1f, 1));

            // Rotated, nested, bordered masks.
            const float rotation = 17;
            float radians = float.DegreesToRadians(rotation);
            float c = MathF.Cos(radians), s = MathF.Sin(radians);
            var translation = new Vector2(40, 34);

            Vector2 toScreen(float x, float y) => new Vector2(c * x - s * y + translation.X, s * x + c * y + translation.Y);
            Quad quad(float x, float y, float w, float h) => new Quad(toScreen(x, y), toScreen(x + w, y), toScreen(x, y + h), toScreen(x + w, y + h));

            var rotated = new Matrix3(
                c, -s, 0,
                s, c, 0,
                -(c * translation.X + s * translation.Y), s * translation.X - c * translation.Y, 1);

            MaskingInfo rotatedMask(float x, float y, float w, float h, float radius, float border)
            {
                var info = mask(new RectangleF(x, y, w, h), rotated, radius, border);
                info.ScreenSpaceAABB = quad(x, y, w, h).AABB;
                return info;
            }

            renderer.PushMaskingInfo(rotatedMask(-30, -24, 60, 48, 10, 3));
            draw(renderer, quad(-34, -28, 68, 56), new Color4(0.2f, 0.2f, 0.6f, 1));
            renderer.PushMaskingInfo(rotatedMask(-24, -18, 48, 36, 6, 0));

            for (int i = 0; i < 6; i++)
                draw(renderer, quad(-22 + i * 8, -6, 6, 10), new Color4(0.1f, 1, 0.3f, 0.6f));

            // A depth-writing draw cannot be clipped in the shader and must fall back to hardware clipping mid-sequence.
            renderer.PushDepthInfo(new DepthInfo(false, true));
            draw(renderer, quad(-30, 8, 60, 6), new Color4(1, 0.2f, 0.8f, 0.7f));
            renderer.PopDepthInfo();

            draw(renderer, quad(-20, -16, 40, 4), new Color4(1, 1, 0.2f, 0.7f));
            renderer.PopMaskingInfo();
            draw(renderer, quad(-10, 14, 20, 20), new Color4(0, 1, 1, 0.5f));
            renderer.PopMaskingInfo();

            // A mask wider than its parent: the parent's scissor must still clip.
            renderer.PushMaskingInfo(mask(new RectangleF(8, 74, 40, 40), Matrix3.Identity, 0, 0));
            renderer.PushMaskingInfo(mask(new RectangleF(0, 70, 64, 50), Matrix3.Identity, 6, 2));
            draw(renderer, new Quad(0, 66, 70, 60), new Color4(0.8f, 0.8f, 0.2f, 0.9f));
            renderer.PopMaskingInfo();
            draw(renderer, new Quad(30, 100, 30, 20), new Color4(0.2f, 0.9f, 0.4f, 0.6f));
            renderer.PopMaskingInfo();

            // A hollow edge effect with a wide blend range and the edge-effect exponents.
            var hollow = mask(new RectangleF(70, 8, 50, 40), Matrix3.Identity, 12, 0);
            hollow.ScreenSpaceAABB = new RectangleI(60, 0, 70, 58);
            hollow.BlendRange = 6;
            hollow.AlphaExponent = 2;
            hollow.CornerExponent = 2.5f;
            hollow.Hollow = true;
            hollow.HollowCornerRadius = 12;
            hollow.EdgeOffset = new Vector2(2, 3);
            renderer.PushMaskingInfo(hollow);
            renderer.DrawQuad(renderer.WhitePixel, new Quad(60, 0, 68, 58), ColourInfo.SingleColour(new Color4(1, 0.6f, 0.2f, 0.9f)), blendRangeOverride: new Vector2(6));
            renderer.PopMaskingInfo();

            // Masks with the scissor test disabled.
            renderer.PushScissorState(false);
            renderer.PushMaskingInfo(mask(new RectangleF(4, 50, 50, 16), Matrix3.Identity, 5, 1));
            draw(renderer, new Quad(0, 46, 60, 24), new Color4(0.6f, 0.3f, 1, 0.8f));
            renderer.PopMaskingInfo();
            renderer.PopScissorState();

            // The offset frame buffer, drawn under a rounded mask.
            renderer.PushMaskingInfo(mask(new RectangleF(70, 60, 48, 48), Matrix3.Identity, 10, 0));
            renderer.DrawFrameBuffer(inner, new Quad(70, 60, 48, 48), ColourInfo.SingleColour(Color4.White));
            renderer.PopMaskingInfo();

            shader.Unbind();
            renderer.PopDepthInfo();
            renderer.PopViewport();
            target.Unbind();
            renderer.FinishFrame();

            // The statistics are reset at the start of each frame.
            clippingFlushes = maskingFlushes.Value + scissorFlushes.Value;

            renderer.WaitUntilIdle();
            using var image = renderer.ExtractFrameBufferData(target);
            Assert.That(image, Is.Not.Null);
            return image!.Pixels.ToArray();
        }

        private static int maxChannelDifference(Rgba32 a, Rgba32 b)
            => Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));

        private static void draw(Renderer renderer, Quad quad, Color4 colour)
            => renderer.DrawQuad(renderer.WhitePixel, quad, ColourInfo.SingleColour(colour));

        private static MaskingInfo mask(RectangleF rect, Matrix3 toMaskingSpace, float radius, float border) => new MaskingInfo
        {
            ScreenSpaceAABB = new RectangleI((int)rect.X, (int)rect.Y, (int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height)),
            MaskingRect = rect,
            ToMaskingSpace = toMaskingSpace,
            CornerRadius = radius,
            CornerExponent = 2,
            BorderThickness = border,
            BorderColour = new ColourInfo
            {
                TopLeft = Color4.Yellow,
                TopRight = Color4.Red,
                BottomLeft = Color4.Blue,
                BottomRight = Color4.Green,
            },
            BlendRange = 1,
            AlphaExponent = 1,
        };
    }
}
