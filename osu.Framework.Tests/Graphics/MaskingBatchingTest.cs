// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Platform;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class MaskingBatchingTest
    {
        private DummyRenderer renderer = null!;
        private RecordingShader shader = null!;
        private RecordingBatch batch = null!;
        private bool wasDrawThread;

        [SetUp]
        public void SetUp()
        {
            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            renderer = new DummyRenderer();
            ((IRenderer)renderer).Initialise(new TestSurface());
            renderer.BeginFrame(new Vector2(100, 100));
            shader = new RecordingShader();
            renderer.BindShader(shader);
            batch = new RecordingBatch(renderer);
        }

        [TearDown]
        public void TearDown()
        {
            renderer.FinishFrame();
            shader.Dispose();
            batch.Dispose();
            ThreadSafety.IsDrawThread = wasDrawThread;
        }

        [Test]
        public void TestEquivalentSiblingMasksStayInOneBatch()
        {
            var mask = createMask();

            for (int i = 0; i < 100; i++)
            {
                renderer.PushMaskingInfo(mask);
                batch.AddQuad();
                renderer.PopMaskingInfo();
            }

            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 400 }));
            Assert.That(shader.Draws[0].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[0].CornerRadius.Value, Is.EqualTo(mask.CornerRadius));
        }

        [Test]
        public void TestEmptyDifferentMaskDoesNotSplitBatch()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            var emptyMask = mask;
            emptyMask.CornerRadius = 17;
            renderer.PushMaskingInfo(emptyMask);
            renderer.PopMaskingInfo();
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 8 }));
        }

        [Test]
        public void TestDifferentShaderParametersSplitBeforeNewVertices()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            mask.CornerRadius = 17;
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
            Assert.That(shader.Draws[0].CornerRadius.Value, Is.EqualTo(4));
            Assert.That(shader.Draws[1].CornerRadius.Value, Is.EqualTo(17));
        }

        [Test]
        public void TestCullingMetadataDoesNotSplitBatch()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            mask.ConservativeScreenSpaceQuad = new Quad(1, 2, 3, 4);
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 8 }));
            Assert.That(renderer.CurrentMaskingInfo.ConservativeScreenSpaceQuad, Is.EqualTo(mask.ConservativeScreenSpaceQuad));
        }

        [Test]
        public void TestDifferentEffectiveScissorsSplitBatch()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            mask.ScreenSpaceAABB = new RectangleI(20, 20, 30, 30);
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestIdenticalMaskStillPushesAndRestoresScissor()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            var parentScissor = renderer.Scissor;
            var narrowScissor = new RectangleI(20, 20, 10, 10);
            renderer.PushScissor(narrowScissor);
            renderer.PushMaskingInfo(mask, true);
            Assert.That(renderer.Scissor, Is.EqualTo(parentScissor));
            renderer.PopMaskingInfo();
            Assert.That(renderer.Scissor, Is.EqualTo(narrowScissor));
            renderer.PopScissor();
            Assert.That(renderer.Scissor, Is.EqualTo(parentScissor));
            renderer.PopMaskingInfo();
            Assert.That(renderer.Scissor, Is.EqualTo(new RectangleI(0, 0, 100, 100)));
        }

        [Test]
        public void TestIdenticalDefaultMaskStillEnablesShaderMasking()
        {
            var mask = renderer.CurrentMaskingInfo;
            batch.AddQuad();
            renderer.PushMaskingInfo(mask);
            Assert.That(renderer.IsMaskingActive, Is.True);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            Assert.That(renderer.IsMaskingActive, Is.False);
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4, 4 }));
            Assert.That(shader.Draws[0].IsMasking.Value, Is.False);
            Assert.That(shader.Draws[1].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[2].IsMasking.Value, Is.False);
        }

        [Test]
        public void TestUnrelatedFlushRetainsSubmittedMask()
        {
            renderer.PushMaskingInfo(createMask());
            batch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.PushScissorState(false);
            Assert.That(shader.Draws[0].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[0].CornerRadius.Value, Is.EqualTo(4));
            renderer.PopScissorState();
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(shader.Draws[1].IsMasking.Value, Is.False);
        }

        [Test]
        public void TestDirectDrawAppliesPendingMaskAfterFlushingOldGeometry()
        {
            renderer.PushMaskingInfo(createMask());
            batch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.DrawVertices(PrimitiveTopology.Triangles, 0, 3);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4 }));
            Assert.That(shader.Draws.Count, Is.EqualTo(2));
            Assert.That(shader.Draws[0].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[1].IsMasking.Value, Is.False);
        }

        private static MaskingInfo createMask() => new MaskingInfo
        {
            ScreenSpaceAABB = new RectangleI(10, 10, 80, 80),
            MaskingRect = new RectangleF(10, 10, 80, 80),
            ToMaskingSpace = Matrix3.Identity,
            CornerRadius = 4,
            CornerExponent = 2.5f,
            BlendRange = 1,
            AlphaExponent = 1,
        };

        private sealed class RecordingBatch : IVertexBatch
        {
            private readonly Renderer renderer;
            private int pendingVertices;
            public readonly List<int> DrawCounts = new List<int>();

            public RecordingBatch(Renderer renderer)
            {
                this.renderer = renderer;
            }

            public int Size => 1000;

            public void AddQuad()
            {
                renderer.SetActiveBatch(this);
                pendingVertices += 4;
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

        private sealed class RecordingShader : IShader
        {
            public readonly List<GlobalUniformData> Draws = new List<GlobalUniformData>();
            public bool IsLoaded => true;
            public bool IsBound => true;
            public void Bind() { }
            public void Unbind() { }
            public void Dispose() { }

            public void BindUniformBlock(string blockName, IUniformBuffer buffer)
                => Draws.Add(((IUniformBuffer<GlobalUniformData>)buffer).Data);
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
