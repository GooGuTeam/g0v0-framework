// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class UntexturedWhitePixelTest
    {
        private static readonly Vector4 untextured_rect = new Vector4(-2, -2, -1, -1);

        private DummyRenderer renderer = null!;
        private IShader shader = null!;
        private Texture texture = null!;
        private readonly List<TexturedVertex2D> vertices = new List<TexturedVertex2D>();
        private bool wasDrawThread;

        [SetUp]
        public void SetUp()
        {
            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            renderer = new DummyRenderer();
            ((IRenderer)renderer).Initialise(new TestSurface());
            renderer.BeginFrame(new Vector2(100, 100));
            shader = ((IRenderer)renderer).CreateShader($"{VertexShaderDescriptor.TEXTURE_2}/{FragmentShaderDescriptor.TEXTURE}", Array.Empty<IShaderPart>());
            renderer.BindShader(shader);
            texture = renderer.CreateTexture(4, 4);
            vertices.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            renderer.FinishFrame();
            texture.Dispose();
            shader.Dispose();
            ThreadSafety.IsDrawThread = wasDrawThread;
        }

        [Test]
        public void TestWhitePixelAfterTextureIsUntextured()
        {
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);
            drawWhite();

            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect == untextured_rect && v.BlendRange == Vector2.Zero));
            Assert.That(vertices[0].TexturePosition, Is.EqualTo(new Vector2(-2, -1)));
            Assert.That(vertices[2].TexturePosition, Is.EqualTo(new Vector2(-1, -2)));
        }

        [Test]
        public void TestWhitePixelWithoutBoundTextureBindsNormally()
        {
            drawWhite();
            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect.X >= 0));
        }

        [Test]
        public void TestWhitePixelAfterFrameBufferChangeBindsNormally()
        {
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);

            using var frameBuffer = renderer.CreateFrameBuffer();
            frameBuffer.Size = new Vector2(10);
            renderer.BindFrameBuffer(frameBuffer);
            drawWhite();
            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect.X >= 0));

            // Binding a texture into the framebuffer makes the untextured path available again.
            vertices.Clear();
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);
            drawWhite();
            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect == untextured_rect));
            renderer.UnbindFrameBuffer(frameBuffer);

            vertices.Clear();
            drawWhite();

            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect.X >= 0));
        }

        [Test]
        public void TestWhitePixelWithOtherShaderBindsNormally()
        {
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);

            var other = ((IRenderer)renderer).CreateShader("Texture2D/Custom", Array.Empty<IShaderPart>());
            renderer.BindShader(other);
            drawWhite();
            renderer.UnbindShader(other);
            other.Dispose();

            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect.X >= 0));
        }

        [Test]
        public void TestOtherTextureIsNeverUntextured()
        {
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);
            renderer.DrawQuad(texture, new Quad(0, 0, 10, 10), ColourInfo.SingleColour(Color4.White), vertexAction: vertices.Add);
            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect.X >= 0));
        }

        [Test]
        public void TestEdgeSmoothnessIsMappedProportionally()
        {
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);
            drawWhite(inflationPercentage: new Vector2(0.25f, 0.5f));

            // Inflation is a fraction of the rectangle, which is a unit square in the untextured space.
            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect == untextured_rect && v.BlendRange == new Vector2(0.25f, 0.5f)));
            Assert.That(vertices[0].TexturePosition, Is.EqualTo(new Vector2(-2.25f, -0.5f)));
            Assert.That(vertices[2].TexturePosition, Is.EqualTo(new Vector2(-0.75f, -2.5f)));
        }

        [Test]
        public void TestBlendRangeOverride()
        {
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);

            drawWhite(blendRangeOverride: new Vector2(3));
            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect == untextured_rect && v.BlendRange == new Vector2(3)));

            vertices.Clear();
            drawWhite(inflationPercentage: new Vector2(0.1f), blendRangeOverride: new Vector2(3));
            Assert.That(vertices, Has.All.Matches<TexturedVertex2D>(v => v.TextureRect.X >= 0));
        }

        [Test]
        public void TestTriangleIsUntextured()
        {
            Assert.That(((IRenderer)renderer).BindTexture(texture), Is.True);
            renderer.DrawTriangle(renderer.WhitePixel, new Triangle(new Vector2(0), new Vector2(10, 0), new Vector2(0, 10)), ColourInfo.SingleColour(Color4.White),
                vertexAction: vertices.Add);
            Assert.That(vertices, Has.Count.EqualTo(4).And.All.Matches<TexturedVertex2D>(v => v.TextureRect == untextured_rect));
        }

        private void drawWhite(Vector2? inflationPercentage = null, Vector2? blendRangeOverride = null)
            => renderer.DrawQuad(renderer.WhitePixel, new Quad(0, 0, 10, 10), ColourInfo.SingleColour(Color4.White), vertexAction: vertices.Add,
                inflationPercentage: inflationPercentage, blendRangeOverride: blendRangeOverride);

        private sealed class TestSurface : IGraphicsSurface
        {
            public IntPtr WindowHandle => IntPtr.Zero;
            public GraphicsSurfaceType Type => GraphicsSurfaceType.Direct3D11;
            public void Initialise() { }
            public System.Drawing.Size GetDrawableSize() => new System.Drawing.Size(100, 100);
        }
    }
}
