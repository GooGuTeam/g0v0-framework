// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.OpenGL.Buffers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.OpenGL.Batches
{
    internal class GLQuadBatch<T> : GLVertexBatch<T>, IQuadBatch<T>
        where T : unmanaged, IEquatable<T>, IVertex
    {
        public GLQuadBatch(GLRenderer renderer, int size, int maxBuffers)
            : base(renderer, size, maxBuffers)
        {
        }

        protected override GLVertexBuffer<T> CreateVertexBuffer(GLRenderer renderer) => new GLQuadBuffer<T>(renderer, Size, BufferUsageHint.DynamicDraw);
    }
}
