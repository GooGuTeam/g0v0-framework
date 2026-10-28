// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering.Deferred
{
    internal interface IDeferredStaticMesh
    {
        void Draw(int chunk, int firstVertex, int count);
    }
}
