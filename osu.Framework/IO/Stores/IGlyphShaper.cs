// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Text;

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// A glyph store that can shape a run of text, rather than only mapping one character at a time.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IGlyphStore"/> so that existing implementations do not need to change; callers
    /// probe for it and fall back to per-character lookups when it is absent.
    /// </remarks>
    internal interface IGlyphShaper
    {
        /// <summary>
        /// Shapes a run of text.
        /// </summary>
        /// <param name="text">The text to shape.</param>
        /// <param name="glyphs">The shaped glyphs, in visual order.</param>
        /// <returns>
        /// <see langword="false"/> if this store cannot shape the run, in which case the caller must fall back to
        /// per-character lookups. A partially covered run is a failure: the per-character path can substitute glyphs
        /// from other fonts, but a shaped run cannot.
        /// </returns>
        bool TryShape(string text, out IReadOnlyList<ShapedGlyph> glyphs);

        /// <summary>
        /// The texture name for a glyph of this font rendered at <paramref name="tier"/>.
        /// </summary>
        string GetTextureName(uint glyphId, int tier);

        /// <summary>
        /// The texture resolution multiplier for a glyph of this font at <paramref name="tier"/>.
        /// </summary>
        float GetTextureScale(int tier);
    }
}
