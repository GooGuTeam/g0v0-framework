// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.IO.Stores;

namespace osu.Framework.Text
{
    internal static class TexturedGlyphLookupStoreExtensions
    {
        internal static ITexturedCharacterGlyph? GetForSize(this ITexturedGlyphLookupStore store, string? fontName, char character, float fontSize)
            => store is FontStore fonts ? fonts.GetForSize(fontName, character, fontSize) : store.Get(fontName, character);
    }
}
