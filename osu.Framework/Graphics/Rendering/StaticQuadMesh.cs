// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.OpenGL.Buffers;
using osu.Framework.Graphics.Rendering.Deferred;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Buffers;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// An immutable, draw-thread-owned quad mesh. Replacing geometry requires a new mesh.
    /// </summary>
    internal abstract class StaticQuadMesh<T> : IDisposable
        where T : unmanaged, IEquatable<T>, IVertex
    {
        protected const int CHUNK_VERTICES = IRenderer.MAX_QUADS * IRenderer.VERTICES_PER_QUAD;
        protected readonly Renderer Renderer;
        private readonly int vertexCount;
        private bool disposed;

        protected StaticQuadMesh(Renderer renderer, int vertexCount)
        {
            Renderer = renderer;
            this.vertexCount = vertexCount;
        }

        public static StaticQuadMesh<T> Create(IRenderer renderer, ReadOnlySpan<T> vertices)
        {
            if (vertices.Length == 0 || vertices.Length % IRenderer.VERTICES_PER_QUAD != 0)
                throw new ArgumentException(@"A mesh must contain complete quads.", nameof(vertices));

            // Resource creation may change bindings. Finish queued geometry before touching them.
            if (renderer is Renderer implementation)
                implementation.FlushCurrentBatch(null);

            return renderer switch
            {
                GLRenderer gl => new GLStaticQuadMesh<T>(gl, vertices),
                VeldridRenderer veldrid => new VeldridStaticQuadMesh<T>(veldrid, vertices),
                DeferredRenderer deferred => new VeldridStaticQuadMesh<T>(deferred, vertices),
                DummyRenderer dummy => new DummyStaticQuadMesh(dummy, vertices.Length),
                _ => throw new NotSupportedException(@"The renderer does not support static quad meshes.")
            };
        }

        public void Draw(int firstVertex, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (firstVertex < 0 || count < 0 || firstVertex > vertexCount - count
                || firstVertex % IRenderer.VERTICES_PER_QUAD != 0 || count % IRenderer.VERTICES_PER_QUAD != 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (count == 0)
                return;

            Renderer.FlushCurrentBatch(null);
            Renderer.ApplyPendingMaskingState();

            while (count > 0)
            {
                int chunk = firstVertex / CHUNK_VERTICES;
                int start = firstVertex % CHUNK_VERTICES;
                int length = Math.Min(count, CHUNK_VERTICES - start);
                DrawChunk(chunk, start, length);
                firstVertex += length;
                count -= length;
            }
        }

        protected abstract void DrawChunk(int chunk, int firstVertex, int count);
        protected abstract void DisposeResources();

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            DisposeResources();
            GC.SuppressFinalize(this);
        }

        private sealed class DummyStaticQuadMesh : StaticQuadMesh<T>
        {
            public DummyStaticQuadMesh(DummyRenderer renderer, int count)
                : base(renderer, count)
            {
            }

            protected override void DrawChunk(int chunk, int firstVertex, int count)
            {
            }

            protected override void DisposeResources()
            {
            }
        }
    }
}
