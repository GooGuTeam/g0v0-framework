// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.OpenGL.Buffers
{
    internal sealed class GLStaticQuadMesh<T> : StaticQuadMesh<T>
        where T : unmanaged, IEquatable<T>, IVertex
    {
        private readonly GLRenderer renderer;
        private readonly List<(int Vao, int Vbo, int Ebo, NativeMemoryTracker.NativeMemoryLease Lease)> chunks = new();

        public unsafe GLStaticQuadMesh(GLRenderer renderer, ReadOnlySpan<T> vertices)
            : base(renderer, vertices.Length)
        {
            this.renderer = renderer;
            try
            {
                for (int start = 0; start < vertices.Length; start += CHUNK_VERTICES)
                {
                    int count = Math.Min(CHUNK_VERTICES, vertices.Length - start);
                    int vao = GL.GenVertexArray();
                    int vbo = GL.GenBuffer();
                    int ebo = GL.GenBuffer();
                    int bytes = checked(count * GLVertexUtils<T>.STRIDE);
                    ushort[] indices = new ushort[count / 4 * 6];
                    for (int v = 0, i = 0; v < count; v += 4, i += 6)
                    {
                        indices[i] = (ushort)v;
                        indices[i + 1] = (ushort)(v + 1);
                        indices[i + 2] = (ushort)(v + 3);
                        indices[i + 3] = (ushort)(v + 2);
                        indices[i + 4] = (ushort)(v + 3);
                        indices[i + 5] = (ushort)(v + 1);
                    }

                    chunks.Add((vao, vbo, ebo, NativeMemoryTracker.AddMemory(this, bytes + indices.Length * sizeof(ushort))));
                    renderer.BindVertexArray(vao);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                    fixed (T* data = vertices.Slice(start, count))
                        GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)bytes, (IntPtr)data, BufferUsageHint.StaticDraw);
                    GLVertexUtils<T>.SetAttributes();
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(indices.Length * sizeof(ushort)), indices, BufferUsageHint.StaticDraw);
                    FrameStatistics.Add(StatisticsCounterType.VerticesUpl, count);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        protected override void DrawChunk(int chunk, int firstVertex, int count)
        {
            renderer.BindVertexArray(chunks[chunk].Vao);
            renderer.DrawVertices(PrimitiveTopology.Triangles, firstVertex / 4 * 6, count / 4 * 6);
        }

        protected override void DisposeResources()
        {
            foreach (var chunk in chunks)
                renderer.ScheduleDisposal(static state =>
                {
                    // Deleting a bound VAO must also invalidate the renderer's cached binding.
                    state.Renderer.BindVertexArray(0);
                    GL.DeleteVertexArray(state.Chunk.Vao);
                    GL.DeleteBuffer(state.Chunk.Vbo);
                    GL.DeleteBuffer(state.Chunk.Ebo);
                    state.Chunk.Lease.Dispose();
                }, (Renderer: renderer, Chunk: chunk));
        }
    }
}
