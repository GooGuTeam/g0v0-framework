// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Deferred;
using osu.Framework.Graphics.Rendering.Deferred.Events;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Veldrid.Vertices;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using Veldrid;
using PrimitiveTopology = osu.Framework.Graphics.Rendering.PrimitiveTopology;

namespace osu.Framework.Graphics.Veldrid.Buffers
{
    internal sealed class VeldridStaticQuadMesh<T> : StaticQuadMesh<T>, IDeferredStaticMesh
        where T : unmanaged, IEquatable<T>, IVertex
    {
        private readonly IVeldridRenderer renderer;
        private readonly List<(DeviceBuffer Buffer, NativeMemoryTracker.NativeMemoryLease Lease)> chunks = new();
        private VeldridIndexBuffer? indexBuffer;

        public VeldridStaticQuadMesh(Renderer renderer, ReadOnlySpan<T> vertices)
            : base(renderer, vertices.Length)
        {
            this.renderer = (IVeldridRenderer)renderer;
            try
            {
                for (int start = 0; start < vertices.Length; start += CHUNK_VERTICES)
                {
                    int count = Math.Min(CHUNK_VERTICES, vertices.Length - start);
                    uint bytes = checked((uint)(count * VeldridVertexUtils<T>.STRIDE));
                    var buffer = this.renderer.Factory.CreateBuffer(new BufferDescription(bytes, BufferUsage.VertexBuffer));
                    chunks.Add((buffer, NativeMemoryTracker.AddMemory(this, bytes)));

                    // These buffers are new and immutable; no previously recorded draw can reference them.
                    if (renderer is VeldridRenderer immediate)
                        immediate.BufferUpdateCommands.UpdateBuffer(buffer, 0, vertices.Slice(start, count));
                    else
                        this.renderer.Device.UpdateBuffer(buffer, 0, vertices.Slice(start, count));

                    FrameStatistics.Add(StatisticsCounterType.VerticesUpl, count);
                }

                if (renderer is DeferredRenderer deferred)
                    indexBuffer = new VeldridIndexBuffer(this.renderer, deferred.Graphics, VeldridIndexLayout.Quad, Math.Min(vertices.Length, CHUNK_VERTICES));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        protected override void DrawChunk(int chunk, int firstVertex, int count)
        {
            if (renderer is DeferredRenderer deferred)
            {
                // Prepare the same global uniforms and statistics as a normal batch, then preserve event order.
                deferred.DrawVertices(PrimitiveTopology.Triangles, firstVertex, count);
                deferred.Context.EnqueueEvent(DrawStaticMeshEvent.Create(deferred, this, chunk, firstVertex, count));
            }
            else
            {
                var immediate = (VeldridRenderer)renderer;
                immediate.BindStaticVertexBuffer(chunks[chunk].Buffer, VeldridVertexUtils<T>.Layout);
                immediate.BindIndexBuffer(VeldridIndexLayout.Quad, Math.Min(CHUNK_VERTICES, firstVertex + count));
                immediate.DrawVertices(PrimitiveTopology.Triangles, firstVertex, count);
            }
        }

        void IDeferredStaticMesh.Draw(int chunk, int firstVertex, int count)
        {
            var graphics = ((DeferredRenderer)renderer).Graphics;
            graphics.SetVertexBuffer(chunks[chunk].Buffer, VeldridVertexUtils<T>.Layout);
            graphics.SetIndexBuffer(indexBuffer!);
            graphics.DrawVertices(global::Veldrid.PrimitiveTopology.TriangleList, firstVertex, count);
        }

        protected override void DisposeResources()
        {
            // Retain the immutable handles until queued commands and deferred events have finished using them.
            foreach (var chunk in chunks)
                renderer.ScheduleDisposal(static c =>
                {
                    c.Buffer.Dispose();
                    c.Lease.Dispose();
                }, chunk);
            indexBuffer?.Dispose();
        }
    }
}
