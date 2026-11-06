// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Primitives;

namespace osu.Framework.Graphics.Textures
{
    internal interface IAtlasPacker
    {
        void Reset(int width, int height);
        bool TryAllocate(int width, int height, out Vector2I position);
    }
}
