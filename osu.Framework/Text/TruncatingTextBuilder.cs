// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE-OSU file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace osu.Framework.Text
{
    public sealed class TruncatingTextBuilder : TextBuilder
    {
        /// <summary>
        /// Indicates whether <see cref="SpriteText.Text"/> has been visually truncated.
        /// </summary>
        public bool IsTruncated { get; private set; }

        private readonly char[]? neverFixedWidthCharacters;
        private readonly char fallbackCharacter;
        private readonly ITexturedGlyphLookupStore store;
        private readonly FontUsage font;
        private readonly string? ellipsisString;
        private readonly bool useFontSizeAsHeight;
        private readonly Vector2 spacing;

        private bool addingEllipsis; // Only used temporarily during the addition of the ellipsis.

        /// <summary>
        /// Creates a new <see cref="TextBuilder"/>.
        /// </summary>
        /// <param name="store">The store from which glyphs are to be retrieved from.</param>
        /// <param name="font">The font to use for glyph lookups from <paramref name="store"/>.</param>
        /// <param name="ellipsisString">The string to be displayed if the text exceeds the allowable text area.</param>
        /// <param name="useFontSizeAsHeight">True to use the provided <see cref="font"/> size as the height for each line. False if the height of each individual glyph should be used.</param>
        /// <param name="startOffset">The offset at which characters should begin being added at.</param>
        /// <param name="spacing">The spacing between characters.</param>
        /// <param name="maxWidth">The maximum width of the resulting text bounds.</param>
        /// <param name="characterList">That list to contain all resulting <see cref="TextBuilderGlyph"/>s.</param>
        /// <param name="neverFixedWidthCharacters">The characters for which fixed width should never be applied.</param>
        /// <param name="fallbackCharacter">The character to use if a glyph lookup fails.</param>
        /// <param name="fixedWidthReferenceCharacter">The character to use to calculate the fixed width width. Defaults to 'm'.</param>
        public TruncatingTextBuilder(ITexturedGlyphLookupStore store, FontUsage font, float maxWidth, string? ellipsisString = null, bool useFontSizeAsHeight = true, Vector2 startOffset = default,
                                     Vector2 spacing = default, List<TextBuilderGlyph>? characterList = null, char[]? neverFixedWidthCharacters = null, char fallbackCharacter = '?', char fixedWidthReferenceCharacter = 'm')
            : base(store, font, maxWidth, useFontSizeAsHeight, startOffset, spacing, characterList, neverFixedWidthCharacters, fallbackCharacter, fixedWidthReferenceCharacter)
        {
            this.store = store;
            this.font = font;
            this.ellipsisString = ellipsisString;
            this.useFontSizeAsHeight = useFontSizeAsHeight;
            this.spacing = spacing;
            this.neverFixedWidthCharacters = neverFixedWidthCharacters;
            this.fallbackCharacter = fallbackCharacter;
        }

        public override void Reset()
        {
            base.Reset();

            IsTruncated = false;
        }

        public override void AddText(string text)
        {
            if (tryAddShapedTruncated(text))
                return;

            base.AddText(text);
        }

        /// <summary>
        /// Adds as much of <paramref name="text"/> as fits, cutting at a cluster boundary.
        /// </summary>
        /// <returns>Whether the text was added, in which case nothing further must be added for it.</returns>
        /// <remarks>
        /// Where the text is cut is decided before any of it is laid out, and the surviving prefix is then shaped on
        /// its own. Cutting glyph by glyph would keep the last character in the form it took while the cut-away text
        /// still followed it, and would drop a whole ligature when only part of it overflowed the available width.
        /// </remarks>
        private bool tryAddShapedTruncated(string text)
        {
            // Truncation keeps the start of the text and drops the end, which is only where the run ends when it reads
            // left to right. Right-to-left text keeps the per-glyph path, which does not need to know where it ends.
            if (addingEllipsis || font.FixedWidth || string.IsNullOrEmpty(text) || !store.SupportsShaping() || BidirectionalText.ContainsRightToLeft(text))
                return false;

            if (!TryShapeText(text, 0, out var glyphs))
                return false;

            if (glyphs.Count == 0 || glyphs[0].ClusterStart != 0)
                return false;

            if (HasAvailableSpace(MeasureShapedRun(glyphs, glyphs.Count)))
            {
                AddShapedGlyphs(glyphs);
                return true;
            }

            bool hasEllipsis = !string.IsNullOrEmpty(ellipsisString);
            int cut = findCut(glyphs, hasEllipsis ? measureEllipsis() + spacing.X : 0);

            // Trailing whitespace would push the ellipsis away from the text it is meant to follow.
            while (cut > 0 && char.IsWhiteSpace(text[cut - 1]))
                cut--;

            IReadOnlyList<ShapedCharacterGlyph>? prefix = null;

            if (cut > 0 && !TryShapeText(text.Substring(0, cut), 0, out prefix))
                return false;

            addingEllipsis = true;

            try
            {
                if (prefix != null)
                    AddShapedGlyphs(prefix);

                if (hasEllipsis)
                    base.AddText(ellipsisString!);
            }
            finally
            {
                addingEllipsis = false;
                IsTruncated = true;
            }

            return true;
        }

        /// <summary>
        /// The width the ellipsis occupies when laid out as its own run.
        /// </summary>
        private float measureEllipsis()
        {
            var builder = new TextBuilder(store, font, float.MaxValue, useFontSizeAsHeight, Vector2.Zero, spacing, new List<TextBuilderGlyph>(), neverFixedWidthCharacters, fallbackCharacter);
            builder.AddText(ellipsisString!);

            return builder.Bounds.X;
        }

        /// <summary>
        /// Finds the number of characters from the start of <paramref name="glyphs"/> that fit, leaving
        /// <paramref name="reserved"/> free for whatever follows.
        /// </summary>
        private int findCut(IReadOnlyList<ShapedCharacterGlyph> glyphs, float reserved)
        {
            int cut = 0;

            for (int i = 0; i < glyphs.Count; i++)
            {
                // Only cut between clusters: a base character without the marks shaped onto it, or half a ligature,
                // is not something that can be drawn.
                if (i + 1 < glyphs.Count && glyphs[i + 1].ClusterStart == glyphs[i].ClusterStart)
                    continue;

                if (!HasAvailableSpace(MeasureShapedRun(glyphs, i + 1) + reserved))
                    break;

                cut = glyphs[i].ClusterEnd;
            }

            return cut;
        }

        protected override bool CanAddCharacters => (base.CanAddCharacters && !IsTruncated) || addingEllipsis;

        protected override bool HasAvailableSpace(float length) => base.HasAvailableSpace(length) || addingEllipsis;

        protected override void OnWidthExceeded()
        {
            if (addingEllipsis)
                return;

            addingEllipsis = true;

            try
            {
                if (string.IsNullOrEmpty(ellipsisString))
                    return;

                // Characters is re-used to reduce allocations, but must be reset after use
                int startIndex = Characters.Count;

                // Compute the ellipsis to find out the size required
                var builder = new TextBuilder(store, font, float.MaxValue, useFontSizeAsHeight, Vector2.Zero, spacing, Characters, neverFixedWidthCharacters, fallbackCharacter);
                builder.AddText(ellipsisString);

                float ellipsisWidth = builder.Bounds.X;
                TextBuilderGlyph firstEllipsisGlyph = builder.Characters[startIndex];

                // Reset the characters list by removing all ellipsis characters
                Characters.RemoveRange(startIndex, Characters.Count - startIndex);

                while (true)
                {
                    RemoveLastCharacter();

                    if (Characters.Count == 0)
                        break;

                    if (Characters[^1].IsWhiteSpace())
                        continue;

                    if (base.HasAvailableSpace(firstEllipsisGlyph.GetKerning(Characters[^1]) + spacing.X + ellipsisWidth))
                        break;
                }

                AddText(ellipsisString);
            }
            finally
            {
                addingEllipsis = false;
                IsTruncated = true;
            }
        }
    }
}
