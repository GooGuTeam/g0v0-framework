// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Rendering.Deferred.Allocation;

namespace osu.Framework.Graphics.Rendering.Deferred.Events
{
    internal readonly record struct DrawStaticMeshEvent(ResourceReference Mesh, int Chunk, int FirstVertex, int Count)
    {
        public static RenderEvent Create(DeferredRenderer renderer, IDeferredStaticMesh mesh, int chunk, int firstVertex, int count)
            => RenderEvent.Create(new DrawStaticMeshEvent(renderer.Context.Reference(mesh), chunk, firstVertex, count));
    }
}
