// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
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
    public class UntexturedWhitePixelGpuTest
    {
        private const int size = 128;

        [Test]
        [Explicit("Requires an offline graphics device and GPU readback.")]
        public void TestUntexturedWhitePixelMatchesSampledWhitePixel()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            var renderer = new VeldridRenderer();

            try
            {
                renderer.InitialiseOffline(size, size);
                using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
                var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
                using var background = renderer.CreateFrameBuffer();
                using var target = renderer.CreateFrameBuffer();
                background.Size = target.Size = new Vector2(size);

                var textureFlushes = GlobalStatistics.Get<int>(nameof(FlushBatchSource), nameof(FlushBatchSource.BindTexture));

                // With texture slots, the sampled white pixel's texture shares the batch as well, so compare both paths with a single slot.
                Renderer.TextureSlotsEnabled = false;

                // The statistic is reset at the start of each frame.
                renderer.UntexturedWhitePixel = false;
                var sampled = render(renderer, background, target, shader);
                int sampledFlushes = textureFlushes.Value;

                renderer.UntexturedWhitePixel = true;
                var untextured = render(renderer, background, target, shader);
                int untexturedFlushes = textureFlushes.Value;

                // The background is a framebuffer texture, so this also covers slots holding textures across framebuffer changes.
                Renderer.TextureSlotsEnabled = true;
                renderer.UntexturedWhitePixel = false;
                var slotted = render(renderer, background, target, shader);
                int slottedFlushes = textureFlushes.Value;

                TestContext.Progress.WriteLine($"texture flushes {sampledFlushes} -> untextured {untexturedFlushes}, texture slots {slottedFlushes}");

                Assert.That(countDiffering(sampled, untextured), Is.Zero, "differing pixels, untextured");
                Assert.That(countDiffering(sampled, slotted), Is.Zero, "differing pixels, texture slots");
                Assert.That(untexturedFlushes, Is.LessThan(sampledFlushes));
                Assert.That(slottedFlushes, Is.LessThan(sampledFlushes));
                Assert.That(untextured, Has.Some.Matches<Rgba32>(pixel => pixel.R > 0 || pixel.G > 0 || pixel.B > 0));
            }
            finally
            {
                Renderer.TextureSlotsEnabled = true;
                renderer.UntexturedWhitePixel = true;

                if (renderer.IsInitialised)
                    renderer.DisposeOffline();
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        private static int countDiffering(Rgba32[] expected, Rgba32[] actual)
        {
            int differing = 0;

            for (int i = 0; i < expected.Length; i++)
            {
                if (!expected[i].Equals(actual[i]))
                    differing++;
            }

            return differing;
        }

        private static Rgba32[] render(VeldridRenderer renderer, IFrameBuffer background, IFrameBuffer target, IShader shader)
        {
            renderer.BeginFrame(new Vector2(size));
            renderer.PushDepthInfo(new DepthInfo(false, false));

            // A non-atlas texture standing in for a beatmap background.
            background.Bind();
            renderer.PushViewport(new RectangleI(0, 0, size, size));
            renderer.Clear(new ClearInfo(new Color4(0.15f, 0.3f, 0.45f, 1)));
            renderer.PopViewport();
            background.Unbind();

            target.Bind();
            renderer.PushViewport(new RectangleI(0, 0, size, size));
            renderer.Clear(new ClearInfo(Color4.Black));
            renderer.SetBlend(BlendingParameters.Mixture);
            shader.Bind();

            void drawBackground(Quad quad) => renderer.DrawFrameBuffer(background, quad, ColourInfo.SingleColour(Color4.White));
            void box(Quad quad, ColourInfo colour, Vector2? inflation = null) => renderer.DrawQuad(renderer.WhitePixel, quad, colour, inflationPercentage: inflation);

            drawBackground(new Quad(0, 0, size, 40));
            box(new Quad(4, 4, 30, 30), ColourInfo.SingleColour(Color4.Orange));
            box(new Quad(20, 10, 60, 20), ColourInfo.GradientHorizontal(new Color4(1, 0, 0, 0.3f), new Color4(0, 1, 0, 0.9f)));

            drawBackground(new Quad(0, 40, size, 40));

            // Rotated box with edge smoothing (inflated quad and texture coordinates, as Sprite does).
            float c = MathF.Cos(0.4f), s = MathF.Sin(0.4f);
            Vector2 centre = new Vector2(90, 60);
            Vector2 rotate(float x, float y) => centre + new Vector2(c * x - s * y, s * x + c * y);
            box(new Quad(rotate(-21, -11), rotate(21, -11), rotate(-21, 11), rotate(21, 11)), ColourInfo.SingleColour(new Color4(1, 1, 1, 0.8f)), new Vector2(1 / 40f, 1 / 20f));

            // Box inside a rounded, bordered mask, touching its edges.
            renderer.PushMaskingInfo(new MaskingInfo
            {
                ScreenSpaceAABB = new RectangleI(10, 50, 50, 30),
                MaskingRect = new RectangleF(10, 50, 50, 30),
                ToMaskingSpace = Matrix3.Identity,
                CornerRadius = 8,
                CornerExponent = 2,
                BorderThickness = 2,
                BorderColour = ColourInfo.SingleColour(Color4.Yellow),
                BlendRange = 1,
                AlphaExponent = 1,
            });
            box(new Quad(10, 50, 50, 30), ColourInfo.GradientVertical(Color4.Purple, Color4.Teal));

            // Clipped opaque-interior path (triangles).
            var clipped = new Quad(5, 45, 60, 40);
            renderer.DrawClipped(ref clipped, renderer.WhitePixel, ColourInfo.SingleColour(new Color4(0.2f, 0.2f, 0.2f, 0.5f)));
            renderer.PopMaskingInfo();

            drawBackground(new Quad(0, 88, size, 40));

            // Hollow glow edge effect, with the blend range override hack.
            renderer.SetBlend(BlendingParameters.Additive);
            renderer.PushMaskingInfo(new MaskingInfo
            {
                ScreenSpaceAABB = new RectangleI(60, 86, 60, 40),
                MaskingRect = new RectangleF(60, 86, 60, 40),
                ToMaskingSpace = Matrix3.Identity,
                CornerRadius = 14,
                CornerExponent = 2,
                BlendRange = 8,
                AlphaExponent = 2,
                Hollow = true,
                HollowCornerRadius = 12,
            });
            renderer.DrawQuad(renderer.WhitePixel, new Quad(60, 86, 60, 40), ColourInfo.SingleColour(new Color4(0.4f, 0.8f, 1, 0.9f)), blendRangeOverride: new Vector2(1));
            renderer.PopMaskingInfo();
            renderer.SetBlend(BlendingParameters.Mixture);

            box(new Quad(8, 96, 40, 24), ColourInfo.SingleColour(new Color4(0, 0, 0, 0.4f)));

            shader.Unbind();
            renderer.PopViewport();
            target.Unbind();
            renderer.PopDepthInfo();
            renderer.FinishFrame();
            renderer.WaitUntilIdle();
            using var image = renderer.ExtractFrameBufferData(target);
            Assert.That(image, Is.Not.Null);
            return image!.Pixels.ToArray();
        }
    }
}
