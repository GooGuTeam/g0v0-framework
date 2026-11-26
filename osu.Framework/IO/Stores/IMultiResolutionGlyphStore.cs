// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// A glyph store that can provide glyph textures at more than one resolution relative to its glyph metrics.
    /// </summary>
    internal interface IMultiResolutionGlyphStore : IGlyphStore
    {
        /// <summary>
        /// Whether glyph textures should be provided at half resolution to save memory.
        /// </summary>
        bool LowResolutionTextures { get; set; }

        /// <summary>
        /// The resolution of the texture provided for <paramref name="character"/> at a requested <paramref name="tier"/>,
        /// relative to the glyph metrics.
        /// </summary>
        /// <param name="character">The character.</param>
        /// <param name="tier">The requested resolution multiplier (1, 2 or 4), chosen by the font store from the drawn font size.</param>
        float GetTextureScale(char character, int tier);

        /// <summary>
        /// The texture lookup name for <paramref name="character"/> at a requested <paramref name="tier"/>.
        /// </summary>
        string GetTextureName(char character, int tier);
    }
}
