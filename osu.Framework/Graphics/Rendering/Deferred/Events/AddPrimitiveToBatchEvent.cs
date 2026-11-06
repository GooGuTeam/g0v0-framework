// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Rendering.Deferred.Allocation;
using osu.Framework.Graphics.Rendering.Vertices;

namespace osu.Framework.Graphics.Rendering.Deferred.Events
{
    internal readonly record struct AddPrimitiveToBatchEvent(ResourceReference VertexBatch, MemoryReference Memory)
    {
        public static RenderEvent Create<T>(DeferredRenderer renderer, DeferredVertexBatch<T> batch, ReadOnlySpan<T> vertices)
            where T : unmanaged, IVertex, IEquatable<T>
            => RenderEvent.Create(new AddPrimitiveToBatchEvent(renderer.Context.ReferenceAdjacent(batch), renderer.Context.AllocateRegion(vertices)));
    }
}
