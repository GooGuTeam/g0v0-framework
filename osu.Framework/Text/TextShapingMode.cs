// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Text
{
    /// <summary>
    /// How text is turned into positioned glyphs before being drawn.
    /// </summary>
    public enum TextShapingMode
    {
        /// <summary>
        /// Each UTF-16 code unit is mapped to a glyph through the font's <c>cmap</c>, with pair kerning applied afterwards.
        /// </summary>
        /// <remarks>
        /// Complex scripts are not supported: contextual forms, ligatures and mark positioning are not applied, and
        /// characters outside the basic plane are looked up as two separate code units.
        /// </remarks>
        Legacy,

        /// <summary>
        /// Text runs are shaped with HarfBuzz, which selects contextual forms and ligatures and positions combining marks.
        /// </summary>
        /// <remarks>
        /// Glyphs are identified by glyph index rather than by character once shaped, so a run of text may not produce
        /// one glyph per character, and the same character may produce different glyphs depending on its neighbours.
        /// </remarks>
        HarfBuzz,
    }
}
