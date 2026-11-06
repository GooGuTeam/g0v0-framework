// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Textures
{
    /// <summary>Allocation policy for newly created atlas pages. Changing policy requires recreating the atlas.</summary>
    public enum AtlasPackingStrategy
    {
        LegacyRows,
        FreeRectangles,
        Etagere,
        Guillotiere,
    }
}
