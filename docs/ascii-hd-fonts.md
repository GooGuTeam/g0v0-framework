# High-definition ASCII bitmap fonts

GlyphStore supports an optional companion font named `<assetName>-ASCII-HD`.
For `Fonts/Inter/Inter-Regular`, provide:

- `Fonts/Inter/Inter-Regular-ASCII-HD.fnt` (binary BMFont format)
- `Fonts/Inter/Inter-Regular-ASCII-HD_0.png` (additional numbered pages if needed)

Generate printable ASCII U+0020–U+007E from the SAME source font, weight and style
as the original, preferably at four times the original pixel size. Do not upscale
an existing PNG: that does not recover outline detail. Export at the higher
resolution with proportional line height and no extra per-glyph padding.

The original font retains control of offsets, advances, baseline and kerning.
The companion's line-height ratio scales its texture dimensions back to the
original layout size. Non-ASCII and missing companion glyphs use the original
bitmap font. A missing or invalid companion does not disable the original font.

Companion resources belong in the application's font resource assembly, alongside
the original bitmap fonts. This framework change alone does not supply higher
resolution font assets or improve an existing low-resolution bitmap.
