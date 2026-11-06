// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Imaging;
using osu.Framework.IO.Stores;
using osu.Framework.Statistics;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class MaskingInteriorRetentionGpuTest
    {
        private const int size = 128;

        [Test]
        [Explicit("Requires an offline graphics device and GPU readback.")]
        public void TestRetainedMaskingMatchesAppliedMasking()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            bool wasPerVertexMasking = PerVertexMasking.Enabled;
            ThreadSafety.IsDrawThread = true;
            var renderer = new VeldridRenderer();

            try
            {
                // Retention applies to shaders that read masking from global uniforms. With per-vertex masking, masking changes
                // do not flush in the first place (see PerVertexMaskingGpuTest).
                PerVertexMasking.Enabled = false;

                renderer.InitialiseOffline(size, size);
                using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
                var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
                using var target = renderer.CreateFrameBuffer();
                target.Size = new Vector2(size);

                var maskingFlushes = GlobalStatistics.Get<int>(nameof(FlushBatchSource), nameof(FlushBatchSource.SetMasking));

                foreach (float rotation in new[] { 0f, 17f })
                {
                    // The statistic is reset at the start of each frame.
                    var applied = render(renderer, target, shader, rotation, false);
                    int appliedFlushes = maskingFlushes.Value;

                    var retained = render(renderer, target, shader, rotation, true);
                    int retainedFlushes = maskingFlushes.Value;

                    TestContext.Progress.WriteLine($"rotation {rotation}: masking flushes {appliedFlushes} -> {retainedFlushes}");
                    Assert.That(retained, Is.EqualTo(applied), $"rotation {rotation}");
                    Assert.That(retainedFlushes, Is.LessThan(appliedFlushes));
                    Assert.That(retained, Has.Some.Matches<Rgba32>(pixel => pixel.R > 0 || pixel.G > 0 || pixel.B > 0));
                }
            }
            finally
            {
                if (renderer.IsInitialised)
                    renderer.DisposeOffline();
                ThreadSafety.IsDrawThread = wasDrawThread;
                PerVertexMasking.Enabled = wasPerVertexMasking;
            }
        }

        private static Rgba32[] render(VeldridRenderer renderer, IFrameBuffer target, IShader shader, float rotation, bool retain)
        {
            renderer.BeginFrame(new Vector2(size));
            target.Bind();
            renderer.PushViewport(new RectangleI(0, 0, size, size));
            renderer.PushDepthInfo(new DepthInfo(false, false));
            renderer.Clear(new ClearInfo(Color4.Black));
            renderer.SetBlend(BlendingParameters.Mixture);
            shader.Bind();

            // Per-vertex submission always applies pending clipping state, giving the reference.
            Action<TexturedVertex2D>? vertexAction = retain ? null : renderer.DefaultQuadBatch.AddAction;

            float radians = float.DegreesToRadians(rotation);
            float c = MathF.Cos(radians), s = MathF.Sin(radians);
            var translation = new Vector2(64, 64);

            Vector2 toScreen(float x, float y) => new Vector2(c * x - s * y + translation.X, s * x + c * y + translation.Y);
            Quad quad(float x, float y, float w, float h) => new Quad(toScreen(x, y), toScreen(x + w, y), toScreen(x, y + h), toScreen(x + w, y + h));

            var toMaskingSpace = new Matrix3(
                c, -s, 0,
                s, c, 0,
                -(c * translation.X + s * translation.Y), s * translation.X - c * translation.Y, 1);

            MaskingInfo mask(float x, float y, float w, float h, float radius, float border) => new MaskingInfo
            {
                ScreenSpaceAABB = quad(x, y, w, h).AABB,
                MaskingRect = new RectangleF(x, y, w, h),
                ToMaskingSpace = toMaskingSpace,
                CornerRadius = radius,
                CornerExponent = 2,
                BorderThickness = border,
                BorderColour = ColourInfo.SingleColour(Color4.Yellow),
                BlendRange = 1,
                AlphaExponent = 1,
            };

            void draw(Quad q, Color4 colour, Vector2? inflation = null)
                => renderer.DrawQuad(renderer.WhitePixel, q, ColourInfo.SingleColour(colour), vertexAction: vertexAction, inflationPercentage: inflation);

            draw(new Quad(0, 0, 8, 8), Color4.White);

            // Panel-like nesting: an outer bordered mask with an inner content mask. Interior geometry (text, icons) directly
            // follows each mask change, so it can stay under the previously applied mask. Edge geometry must still apply its own.
            renderer.PushMaskingInfo(mask(-50, -30, 100, 60, 12, 2));
            draw(quad(-50, -30, 100, 60), new Color4(0.2f, 0.2f, 0.5f, 1));

            renderer.PushMaskingInfo(mask(-40, -22, 80, 44, 10, 0));

            for (int i = 0; i < 6; i++)
                draw(quad(-25 + i * 8, -6, 6, 10), new Color4(0.1f, 1, 0.3f, 0.6f), new Vector2(0.1f));

            // Straight-edge bands, just inside the unaffected threshold (blend range 1 plus the 1 unit margin).
            draw(quad(-28, -19.9f, 56, 3), new Color4(1, 1, 0.2f, 0.7f));
            draw(quad(-37.9f, -10, 3, 20), new Color4(1, 0.2f, 1, 0.7f), new Vector2(0.05f));
            draw(quad(34.9f, -10, 3, 20), new Color4(0.2f, 1, 1, 0.7f));

            renderer.PushMaskingInfo(mask(-20, 6, 30, 12, 5, 0));
            draw(quad(-12, 9, 6, 6), Color4.White);
            draw(quad(-20, 6, 30, 12), new Color4(1, 0.5f, 0, 0.7f));
            renderer.PopMaskingInfo();

            draw(quad(-40, -22, 80, 44), new Color4(0.5f, 0.1f, 0.1f, 0.5f));
            renderer.PopMaskingInfo();

            draw(quad(-10, -8, 20, 4), new Color4(0, 1, 1, 0.5f));
            draw(quad(-10, -28, 20, 4), new Color4(0, 1, 1, 0.5f));
            renderer.PopMaskingInfo();

            draw(new Quad(120, 120, 8, 8), Color4.White);

            shader.Unbind();
            renderer.PopDepthInfo();
            renderer.PopViewport();
            target.Unbind();
            renderer.FinishFrame();
            renderer.WaitUntilIdle();
            using var image = renderer.ExtractFrameBufferData(target);
            Assert.That(image, Is.Not.Null);
            return image!.Pixels.ToArray();
        }
    }
}
