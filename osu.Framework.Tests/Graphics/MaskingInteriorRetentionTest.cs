// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Platform;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class MaskingInteriorRetentionTest
    {
        private DummyRenderer renderer = null!;
        private IShader shader = null!;
        private RecordingQuadBatch batch = null!;
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
            batch = new RecordingQuadBatch(renderer);
        }

        [TearDown]
        public void TearDown()
        {
            renderer.FinishFrame();
            shader.Dispose();
            ThreadSafety.IsDrawThread = wasDrawThread;
        }

        [Test]
        public void TestInteriorQuadKeepsParentMask()
        {
            renderer.PushMaskingInfo(createMask(10, 80));
            addQuad(new Quad(12, 12, 4, 4));

            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(40, 40, 10, 10));
            renderer.PopMaskingInfo();

            addQuad(new Quad(12, 12, 4, 4));
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 12 }));
        }

        [Test]
        public void TestInteriorQuadKeepsUnmaskedState()
        {
            addQuad(new Quad(0, 0, 4, 4));

            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(40, 40, 10, 10));
            renderer.PopMaskingInfo();

            addQuad(new Quad(0, 0, 4, 4));
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 12 }));
        }

        [Test]
        public void TestQuadAlongStraightEdgeKeepsMask()
        {
            renderer.PushMaskingInfo(createMask(10, 80));
            addQuad(new Quad(12, 12, 4, 4));

            // Within the corner radius of the top and bottom edges, but clear of the corner squares.
            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(40, 23, 10, 54));
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 8 }));
        }

        [Test]
        public void TestQuadInsideBlendRangeAppliesMask()
        {
            renderer.PushMaskingInfo(createMask(10, 80));
            addQuad(new Quad(12, 12, 4, 4));

            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(40, 20.5f, 10, 10));
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestQuadOverlappingCornerAppliesMask()
        {
            renderer.PushMaskingInfo(createMask(10, 80));
            addQuad(new Quad(12, 12, 4, 4));

            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(20, 20, 10, 10));
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestQuadOverlappingBorderAppliesMask()
        {
            addQuad(new Quad(0, 0, 4, 4));

            var mask = createMask(20, 60);
            mask.BorderThickness = 15;
            renderer.PushMaskingInfo(mask);
            addQuad(new Quad(30, 40, 10, 10));
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestHollowMaskAppliesMask()
        {
            addQuad(new Quad(0, 0, 4, 4));

            var mask = createMask(20, 60);
            mask.Hollow = true;
            renderer.PushMaskingInfo(mask);
            addQuad(new Quad(40, 40, 10, 10));
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestQuadOutsidePendingScissorAppliesMask()
        {
            addQuad(new Quad(0, 0, 4, 4));

            // The shader mask is wide, but the scissor clips the quad.
            var mask = createMask(10, 80);
            mask.ScreenSpaceAABB = new RectangleI(40, 40, 5, 5);
            renderer.PushMaskingInfo(mask);
            addQuad(new Quad(40, 40, 10, 10));
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestVertexBlendRangeRequiresMatchingAlphaExponent()
        {
            renderer.PushMaskingInfo(createMask(10, 80));
            addQuad(new Quad(12, 12, 4, 4));

            var mask = createMask(20, 60);
            mask.AlphaExponent = 2;
            renderer.PushMaskingInfo(mask);
            addQuad(new Quad(40, 40, 10, 10), new Vector2(0.1f));
            renderer.PopMaskingInfo();

            renderer.PushMaskingInfo(mask);
            addQuad(new Quad(40, 40, 10, 10));
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            // The blended quad needs the alpha exponent; the unblended one is unaffected by it.
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 8 }));
        }

        [Test]
        public void TestVertexBlendRangeWithMatchingAlphaExponentKeepsMask()
        {
            renderer.PushMaskingInfo(createMask(10, 80));
            addQuad(new Quad(12, 12, 4, 4), new Vector2(0.1f));

            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(40, 40, 10, 10), new Vector2(0.1f));
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 8 }));
        }

        [Test]
        public void TestOtherShaderAppliesMask()
        {
            var other = ((IRenderer)renderer).CreateShader("Texture2D/Custom", Array.Empty<IShaderPart>());
            renderer.BindShader(other);

            addQuad(new Quad(0, 0, 4, 4));
            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(40, 40, 10, 10));
            renderer.FlushCurrentBatch(null);

            renderer.UnbindShader(other);
            other.Dispose();

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestRetainedStateIsAppliedForNextIncompatibleQuad()
        {
            renderer.PushMaskingInfo(createMask(10, 80));
            addQuad(new Quad(12, 12, 4, 4));

            renderer.PushMaskingInfo(createMask(20, 60));
            addQuad(new Quad(40, 40, 10, 10));
            addQuad(new Quad(20, 20, 10, 10));
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 8, 4 }));
            Assert.That(renderer.IsMaskingActive, Is.True);
        }

        private void addQuad(Quad quad, Vector2 blendRange = default)
        {
            Span<TexturedVertex2D> vertices = stackalloc TexturedVertex2D[IRenderer.VERTICES_PER_QUAD];
            vertices[0] = new TexturedVertex2D(renderer) { Position = quad.BottomLeft, BlendRange = blendRange };
            vertices[1] = new TexturedVertex2D(renderer) { Position = quad.BottomRight, BlendRange = blendRange };
            vertices[2] = new TexturedVertex2D(renderer) { Position = quad.TopRight, BlendRange = blendRange };
            vertices[3] = new TexturedVertex2D(renderer) { Position = quad.TopLeft, BlendRange = blendRange };

            Assert.That(RendererExtensions.TryAddQuadRetainingMasking(renderer, batch, vertices, quad, blendRange), Is.True);
        }

        private static MaskingInfo createMask(float position, float size) => new MaskingInfo
        {
            ScreenSpaceAABB = new RectangleI((int)position, (int)position, (int)size, (int)size),
            MaskingRect = new RectangleF(position, position, size, size),
            ToMaskingSpace = Matrix3.Identity,
            CornerRadius = 8,
            CornerExponent = 2,
            BlendRange = 1,
            AlphaExponent = 1,
        };

        private sealed class RecordingQuadBatch : IVertexBatch, IQuadBatch<TexturedVertex2D>
        {
            private readonly Renderer renderer;
            private int pendingVertices;
            public readonly List<int> DrawCounts = new List<int>();

            public RecordingQuadBatch(Renderer renderer)
            {
                this.renderer = renderer;
            }

            public int Size => 1000;

            public bool TryAddQuad(ReadOnlySpan<TexturedVertex2D> vertices)
            {
                renderer.SetActiveBatch(this);
                pendingVertices += vertices.Length;
                return true;
            }

            public int Draw()
            {
                int count = pendingVertices;
                pendingVertices = 0;

                if (count > 0)
                {
                    renderer.DrawBatchVertices(PrimitiveTopology.Triangles, 0, count);
                    DrawCounts.Add(count);
                }

                return count;
            }

            void IVertexBatch.ResetCounters() => pendingVertices = 0;
            public void Dispose() { }
        }

        private sealed class TestSurface : IGraphicsSurface
        {
            public IntPtr WindowHandle => IntPtr.Zero;
            public GraphicsSurfaceType Type => GraphicsSurfaceType.Direct3D11;
            public void Initialise() { }
            public System.Drawing.Size GetDrawableSize() => new System.Drawing.Size(100, 100);
        }
    }
}
