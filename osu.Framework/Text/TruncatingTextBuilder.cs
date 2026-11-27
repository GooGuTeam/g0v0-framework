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
            if (addingEllipsis || !CanAddCharacters || font.FixedWidth || string.IsNullOrEmpty(text) || !store.SupportsShaping() || BidirectionalText.ContainsRightToLeft(text))
                return false;

            // Cluster numbers continue after the glyphs already laid out. Numbering each append from zero would make
            // a cluster of this append indistinguishable from one of an earlier append once the text is cut back to
            // make room for the ellipsis, and removing it would then take the earlier text with it.
            int clusterBase = Characters.Count;

            if (!TryShapeText(text, clusterBase, out var glyphs))
                return false;

            if (glyphs.Count == 0 || glyphs[0].ClusterStart != clusterBase)
                return false;

            if (HasAvailableSpace(MeasureShapedRun(glyphs, glyphs.Count)))
            {
                AddShapedGlyphs(glyphs);
                return true;
            }

            bool hasEllipsis = !string.IsNullOrEmpty(ellipsisString);

            // Space to keep free for the ellipsis, including the spacing that separates it from the text before it.
            // No kerning is involved: shaped glyphs carry none, and the ellipsis is separated from the prefix by
            // shaping rather than by a pair lookup.
            float reserved = hasEllipsis ? measureEllipsis() + spacing.X : 0;

            // A cluster boundary that fits in the full run does not guarantee that the prefix ending there still fits:
            // shaping the prefix on its own drops the kerning its last glyph had against the text that was cut away,
            // which widens it. So the longest candidate is tried first and shorter ones are tried after it.
            var cuts = findCuts(glyphs, clusterBase, reserved);

            for (int i = cuts.Count - 1; i >= 0; i--)
            {
                int cut = trimTrailingWhitespace(text, cuts[i]);

                if (cut == 0)
                    continue;

                if (!TryShapeText(text.Substring(0, cut), clusterBase, out var prefix))
                    return false;

                if (!HasAvailableSpace(MeasureShapedRun(prefix, prefix.Count) + reserved))
                    continue;

                addPrefixAndEllipsis(prefix, hasEllipsis);
                return true;
            }

            // Nothing of the new text fits. The ellipsis still has to be drawn within the available width, so what is
            // already laid out is cut back until there is room for it, as OnWidthExceeded does.
            if (hasEllipsis)
            {
                while (Characters.Count > 0)
                {
                    RemoveLastCharacter();

                    if (Characters.Count == 0)
                        break;

                    if (Characters[^1].IsWhiteSpace())
                        continue;

                    if (base.HasAvailableSpace(reserved))
                        break;
                }
            }

            addPrefixAndEllipsis(null, hasEllipsis);
            return true;
        }

        /// <summary>
        /// Adds the surviving prefix and the ellipsis, and marks the text as truncated.
        /// </summary>
        private void addPrefixAndEllipsis(IReadOnlyList<ShapedCharacterGlyph>? prefix, bool hasEllipsis)
        {
            // Width checks are suspended while the ellipsis is added: the space for it has already been reserved, and
            // an ellipsis that does not fit even on an empty line still has to be drawn rather than dropped.
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
        }

        /// <summary>
        /// Moves <paramref name="cut"/> back over the whitespace that ends the kept prefix.
        /// </summary>
        /// <remarks>
        /// Trailing whitespace would push the ellipsis away from the text it is meant to follow.
        /// </remarks>
        private static int trimTrailingWhitespace(string text, int cut)
        {
            while (cut > 0 && char.IsWhiteSpace(text[cut - 1]))
                cut--;

            return cut;
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
        /// The number of characters that fit, one entry per cluster boundary, in increasing order.
        /// </summary>
        /// <param name="glyphs">The shaped run, shaped as a whole.</param>
        /// <param name="clusterBase">The cluster number given to the first character of the run.</param>
        /// <param name="reserved">Space to leave free for whatever follows the run.</param>
        private List<int> findCuts(IReadOnlyList<ShapedCharacterGlyph> glyphs, int clusterBase, float reserved)
        {
            var cuts = new List<int>();
            float width = 0;

            for (int i = 0; i < glyphs.Count; i++)
            {
                // Accumulating the advances as the run is walked keeps this linear in its length, rather than
                // re-measuring every candidate prefix.
                if (i > 0 || !CurrentLineIsEmpty)
                    width += spacing.X;

                width += glyphs[i].Glyph.XAdvance * font.Size;

                // Only cut between clusters: a base character without the marks shaped onto it, or half a ligature,
                // is not something that can be drawn.
                if (i + 1 < glyphs.Count && glyphs[i + 1].ClusterStart == glyphs[i].ClusterStart)
                    continue;

                if (!HasAvailableSpace(width + reserved))
                    break;

                cuts.Add(glyphs[i].ClusterEnd - clusterBase);
            }

            return cuts;
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
