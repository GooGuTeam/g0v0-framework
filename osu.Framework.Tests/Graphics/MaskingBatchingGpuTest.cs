// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class MaskingBatchingGpuTest
    {
        [Test]
        [Explicit("Requires an offline graphics device and GPU readback.")]
        public void TestDeferredMaskChangesMatchEagerChanges()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            var renderer = new VeldridRenderer();

            try
            {
                renderer.InitialiseOffline(64, 64);
                using var shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
                var shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
                using var target = renderer.CreateFrameBuffer();
                target.Size = new Vector2(64);
                var eager = render(renderer, target, shader, true);
                var deferred = render(renderer, target, shader, false);
                Assert.That(deferred, Is.EqualTo(eager));
                Assert.That(deferred, Has.Some.Matches<Rgba32>(pixel => pixel.R > 0 || pixel.G > 0 || pixel.B > 0));
            }
            finally
            {
                if (renderer.IsInitialised)
                    renderer.DisposeOffline();
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        private static Rgba32[] render(VeldridRenderer renderer, IFrameBuffer target, IShader shader, bool eager)
        {
            renderer.BeginFrame(new Vector2(64));
            target.Bind();
            renderer.PushViewport(new RectangleI(0, 0, 64, 64));
            renderer.PushDepthInfo(new DepthInfo(false, false));
            renderer.Clear(new ClearInfo(Color4.Black));
            renderer.SetBlend(BlendingParameters.Mixture);
            shader.Bind();

            var mask = new MaskingInfo
            {
                ScreenSpaceAABB = new RectangleI(8, 8, 48, 48),
                MaskingRect = new RectangleF(8, 8, 48, 48),
                ToMaskingSpace = Matrix3.Identity,
                CornerRadius = 12,
                CornerExponent = 2,
                BlendRange = 1,
                AlphaExponent = 1,
            };

            // Overlapping translucent siblings exercise ordering as well as clipping.
            for (int i = 0; i < 4; i++)
            {
                renderer.PushMaskingInfo(mask);
                if (eager)
                    renderer.ApplyPendingMaskingState();
                renderer.DrawQuad(renderer.WhitePixel, new Quad(0, i * 8, 64, 40), ColourInfo.SingleColour(new Color4(1f, i / 4f, 0.3f, 0.4f)));
                renderer.PopMaskingInfo();
                if (eager)
                    renderer.ApplyPendingMaskingState();
            }

            // A different scissor and hollow rounded mask must retain a draw boundary.
            mask.ScreenSpaceAABB = new RectangleI(16, 16, 32, 32);
            mask.Hollow = true;
            mask.HollowCornerRadius = 6;
            renderer.PushMaskingInfo(mask);
            if (eager)
                renderer.ApplyPendingMaskingState();
            renderer.DrawQuad(renderer.WhitePixel, new Quad(0, 0, 64, 64), ColourInfo.SingleColour(Color4.Blue));
            renderer.PopMaskingInfo();
            if (eager)
                renderer.ApplyPendingMaskingState();
            renderer.DrawQuad(renderer.WhitePixel, new Quad(0, 0, 4, 4), ColourInfo.SingleColour(Color4.White));

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
