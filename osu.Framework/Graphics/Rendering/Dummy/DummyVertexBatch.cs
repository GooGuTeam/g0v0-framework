// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Rendering.Vertices;

namespace osu.Framework.Graphics.Rendering.Dummy
{
    internal class DummyVertexBatch<T> : IVertexBatch<T>, IQuadBatch<T>
        where T : unmanaged, IEquatable<T>, IVertex
    {
        public void Dispose()
        {
        }

        public int Size => 0;

        public int Draw() => 0;

        void IVertexBatch.ResetCounters()
        {
        }

        public Action<T> AddAction { get; } = _ => { };

        public void Add(T vertex)
        {
        }

        public bool TryAddQuad(ReadOnlySpan<T> vertices)
        {
            if (vertices.Length != IRenderer.VERTICES_PER_QUAD)
                throw new ArgumentException(@"A quad must contain exactly four vertices.", nameof(vertices));

            return true;
        }
    }
}
