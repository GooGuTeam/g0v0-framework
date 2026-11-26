// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE-OSU file in the repository root for full licence text.

using System.Runtime.CompilerServices;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Text
{
    /// <summary>
    /// A <see cref="ITexturedCharacterGlyph"/> provided as final output from a <see cref="TextBuilder"/>.
    /// </summary>
    public struct TextBuilderGlyph : ITexturedCharacterGlyph
    {
        public readonly Texture Texture => Glyph.Texture;
        public readonly float XOffset => ((fixedWidth - Glyph.Width) / 2 ?? Glyph.XOffset) * textSize;
        public readonly float XAdvance => (fixedWidth ?? Glyph.XAdvance) * textSize;
        public readonly float Width => Glyph.Width * textSize;
        public readonly char Character => Glyph.Character;

        public readonly float YOffset
        {
            get
            {
                if (useFontSizeAsHeight)
                    return Glyph.YOffset * textSize;

                return 0;
            }
        }

        public readonly float Baseline
        {
            get
            {
                if (useFontSizeAsHeight)
                    return Glyph.Baseline * textSize;

                return (Glyph.Baseline - Glyph.YOffset) * textSize;
            }
        }

        public readonly float Height
        {
            get
            {
                // Space characters typically have heights that exceed the height of all other characters in the font
                // Thus, the height is forced to 0 such that only non-whitespace character heights are considered
                if (Glyph.IsWhiteSpace())
                    return 0;

                return Glyph.Height * textSize;
            }
        }

        public readonly ITexturedCharacterGlyph Glyph;

        /// <summary>
        /// The rectangle for the character to be drawn in.
        /// </summary>
        public RectangleF DrawRectangle { get; internal set; }

        /// <summary>
        /// The top position of the line where this glyph will be placed on.
        /// </summary>
        public float LinePosition { get; internal set; }

        /// <summary>
        /// Whether this is the first character on a new line.
        /// </summary>
        public bool OnNewLine { get; internal set; }

        /// <summary>
        /// The index of the first character of the cluster this glyph was shaped from.
        /// </summary>
        /// <remarks>
        /// Zero for a glyph that came from a per-character lookup, which is also why <see cref="ClusterLength"/>
        /// has to be checked before treating two glyphs as belonging together.
        /// </remarks>
        internal readonly int ClusterStart;

        /// <summary>
        /// The number of characters in the cluster this glyph was shaped from, or zero for a glyph that came from a
        /// per-character lookup and therefore represents exactly one character.
        /// </summary>
        internal readonly int ClusterLength;

        private readonly float textSize;
        private readonly float? fixedWidth;
        private readonly bool useFontSizeAsHeight;

        internal TextBuilderGlyph(ITexturedCharacterGlyph glyph, float textSize, float? fixedWidth = null, bool useFontSizeAsHeight = true, int clusterStart = 0, int clusterLength = 0)
        {
            this = default;
            this.textSize = textSize;
            this.fixedWidth = fixedWidth;
            this.useFontSizeAsHeight = useFontSizeAsHeight;

            Glyph = glyph;
            ClusterStart = clusterStart;
            ClusterLength = clusterLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly float GetKerning<T>(T lastGlyph)
            where T : ICharacterGlyph
            => fixedWidth != null ? 0 : Glyph.GetKerning(lastGlyph);
    }
}
