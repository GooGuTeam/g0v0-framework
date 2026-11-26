// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.IO.Stores;

namespace osu.Framework.Text
{
    internal static class TexturedGlyphLookupStoreExtensions
    {
        internal static ITexturedCharacterGlyph? GetForSize(this ITexturedGlyphLookupStore store, string? fontName, char character, float fontSize)
            => store is FontStore fonts ? fonts.GetForSize(fontName, character, fontSize) : store.Get(fontName, character);

        /// <summary>
        /// Shapes a run of text, if the store is able to.
        /// </summary>
        /// <param name="store">The store to shape with.</param>
        /// <param name="fontName">The font to shape with, matched like <see cref="ITexturedGlyphLookupStore.Get"/>.</param>
        /// <param name="text">The text to shape.</param>
        /// <param name="fontSize">The size the text will be drawn at, which selects the glyph texture resolution.</param>
        /// <param name="glyphs">The shaped glyphs, one per shaped output glyph.</param>
        /// <returns><see langword="false"/> if the store cannot shape this run.</returns>
        internal static bool TryShape(this ITexturedGlyphLookupStore store, string? fontName, string text, float fontSize, out IReadOnlyList<ITexturedCharacterGlyph> glyphs)
        {
            if (store is FontStore fonts)
                return fonts.TryShape(fontName, text, fontSize, out glyphs);

            glyphs = Array.Empty<ITexturedCharacterGlyph>();
            return false;
        }
    }
}
