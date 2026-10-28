// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Rendering.Deferred.Allocation;
using osu.Framework.Graphics.Rendering.Deferred.Events;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Veldrid.Buffers;

namespace osu.Framework.Graphics.Rendering.Deferred
{
    internal class DeferredVertexBatch<TVertex> : IVertexBatch<TVertex>, IDeferredVertexBatch, IQuadBatch<TVertex>
        where TVertex : unmanaged, IEquatable<TVertex>, IVertex
    {
        // Primitive assembly belongs to this batch, not other renderers using the same vertex type.
        private readonly TVertex[] currentPrimitive = new TVertex[4];
        private int currentPrimitiveSize;

        public Action<TVertex> AddAction { get; }

        private readonly DeferredRenderer renderer;
        private readonly PrimitiveTopology topology;
        private readonly VeldridIndexLayout indexLayout;
        private readonly int primitiveSize;

        private int currentDrawCount;

        public DeferredVertexBatch(DeferredRenderer renderer, PrimitiveTopology topology, VeldridIndexLayout indexLayout)
        {
            this.renderer = renderer;

            this.topology = topology;
            this.indexLayout = indexLayout;

            if (this.indexLayout == VeldridIndexLayout.Linear)
            {
                switch (this.topology)
                {
                    case PrimitiveTopology.Points:
                        primitiveSize = 1;
                        break;

                    case PrimitiveTopology.Lines:
                        primitiveSize = 2;
                        break;

                    case PrimitiveTopology.Triangles:
                        primitiveSize = 3;
                        break;

                    case PrimitiveTopology.LineStrip:
                    case PrimitiveTopology.TriangleStrip:
                        throw new NotImplementedException($"Topology '{topology}' is not yet implemented for this renderer.");

                    default:
                        throw new ArgumentOutOfRangeException(nameof(topology));
                }
            }
            else
                primitiveSize = 4;

            AddAction = ((IVertexBatch<TVertex>)this).Add;
        }

        public void Write(in MemoryReference primitive)
            => renderer.Context.VertexManager.Write<TVertex>(primitive);

        public void Draw(int count)
            => renderer.Context.VertexManager.Draw<TVertex>(count, topology, indexLayout, primitiveSize);

        int IVertexBatch.Size
            => int.MaxValue;

        int IVertexBatch.Draw()
        {
            int count = currentDrawCount;
            currentDrawCount = 0;

            if (count == 0)
                return 0;

            renderer.DrawBatchVertices(topology, 0, count);
            renderer.Context.EnqueueEvent(FlushEvent.Create(renderer, this, count));
            return count;
        }

        void IVertexBatch.ResetCounters()
        {
            currentPrimitiveSize = 0;
            currentDrawCount = 0;
        }

        void IVertexBatch<TVertex>.Add(TVertex vertex)
        {
            renderer.SetActiveBatch(this);
            renderer.StampMaskingIndex(ref vertex);

            currentPrimitive[currentPrimitiveSize] = vertex;

            if (++currentPrimitiveSize == primitiveSize)
            {
                renderer.Context.EnqueueEvent(AddPrimitiveToBatchEvent.Create(renderer, this, currentPrimitive.AsSpan()[..primitiveSize]));
                currentPrimitiveSize = 0;
            }

            currentDrawCount++;
        }

        public bool TryAddQuad(ReadOnlySpan<TVertex> vertices)
        {
            if (vertices.Length != IRenderer.VERTICES_PER_QUAD)
                throw new ArgumentException(@"A quad must contain exactly four vertices.", nameof(vertices));

            if (indexLayout != VeldridIndexLayout.Quad || currentPrimitiveSize != 0)
                return false;

            renderer.SetActiveBatch(this);
            Span<TVertex> scratch = stackalloc TVertex[IRenderer.VERTICES_PER_QUAD];
            ReadOnlySpan<TVertex> stamped = renderer.StampMaskingIndex(vertices, scratch);

            // The event copies the input before returning, so caller-owned stack memory is safe.
            renderer.Context.EnqueueEvent(AddPrimitiveToBatchEvent.Create(renderer, this, stamped));
            currentDrawCount += IRenderer.VERTICES_PER_QUAD;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
