// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Text
{
    /// <summary>
    /// A single glyph produced by shaping a run of text, with its positioning already resolved.
    /// </summary>
    /// <remarks>
    /// Metrics are expressed in the same pixel space as <see cref="CharacterGlyph"/> at texture scale 1, so a shaped
    /// glyph can be substituted for a per-character lookup without changing how <see cref="TextBuilder"/> lays text out.
    /// </remarks>
    internal readonly struct ShapedGlyph
    {
        /// <summary>
        /// The glyph index within the font.
        /// </summary>
        public readonly uint GlyphId;

        /// <summary>
        /// The first character of the cluster this glyph was shaped from.
        /// </summary>
        /// <remarks>
        /// Only used to identify the glyph to the layout code (whitespace detection, fixed-width exemptions); a single
        /// glyph may represent several characters and a character may produce several glyphs.
        /// </remarks>
        public readonly char Character;

        /// <summary>
        /// The horizontal distance to advance the pen by.
        /// </summary>
        public readonly float XAdvance;

        /// <summary>
        /// The horizontal offset of the glyph from the pen position, including any mark attachment.
        /// </summary>
        public readonly float XOffset;

        /// <summary>
        /// The vertical offset of the glyph from the top of the line, positive downwards.
        /// </summary>
        public readonly float YOffset;

        public ShapedGlyph(uint glyphId, char character, float xAdvance, float xOffset, float yOffset)
        {
            GlyphId = glyphId;
            Character = character;
            XAdvance = xAdvance;
            XOffset = xOffset;
            YOffset = yOffset;
        }
    }
}
