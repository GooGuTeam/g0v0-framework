# Text shaping

`FrameworkSetting.TextShapingMode` selects how a line of text becomes glyphs.

| Value | Behaviour |
| --- | --- |
| `Legacy` (default) | One glyph per UTF-16 code unit, looked up in the font's character map, with kerning applied between neighbouring characters. |
| `HarfBuzz` | Each direction run is shaped with HarfBuzz from the same font data FreeType rasterises, so ligatures, mark positioning and cursive joining come from the font. |

The mode is read when `FontStore` is created, so changing it takes effect on the
next start. It does not affect text that is laid out on a grid (`FixedWidth`
fonts), and it does not change how glyphs are drawn.

## What changes

A run is shaped with one font. If that font does not cover every character in
the run, the whole run goes back to per-character lookup, which is the only path
that can substitute glyphs from another font. A run that fails to shape at all
(no native library, unreadable font data) is also retried per character, and
shaping is then disabled for that font rather than retried on every frame.

Glyphs are fetched by index (`Font/<name>/#<id>` texture names) instead of by
character, at the same resolution tier the character path would use.

Text is normalised to Unicode form C before layout, once per text change, so
decomposed and precomposed spellings of the same text are laid out identically
and compare equal in application code. Normalisation also runs before word
splitting, because the splitter breaks after CJK characters and would otherwise
separate a kana from its voiced sound mark.

## Clusters

Shaping does not map characters to glyphs one to one. A ligature is several
characters drawn as one glyph; a base with its marks is one character drawn as
several glyphs. Every shaped glyph therefore carries the character range of the
cluster it belongs to, and layout treats a cluster as a unit: removing the last
character removes its whole cluster, and truncation only cuts between clusters.

Truncation decides where to cut before laying anything out, and shapes the
surviving prefix on its own, so the last kept character is not left in the form
and spacing it had while the cut-away text still followed it.

## Direction

Shaping handles one direction at a time, so a line is split into runs of a single
direction first. The paragraph direction comes from its first strong character;
a character with no direction of its own takes the direction of the text before
it. A right-to-left paragraph places its runs from right to left.

Digits read left to right even inside right-to-left text.

This is a subset of UAX #9: explicit embedding controls, bracket pairing and the
full weak-type resolution rules are not implemented. Truncation only cuts shaped
text that reads left to right and falls back to per-glyph removal otherwise,
because the end of the text is only the end of the run in that direction.

## Not covered

- Colour emoji. Glyphs are loaded with `FT_LOAD_NO_BITMAP` and the atlas is a
  single-channel `R8Float` texture, which keeps CBDT/sbix bitmap glyphs and COLR
  layers out. Emoji that the font draws as an outline are shaped normally.
- Shaped runs are not cached: every text change shapes again and allocates new
  glyph objects. Textures are cached by name, so only the shaping and the
  allocations repeat.

## Tests

`osu.Framework.Tests/Text/ShapedTextBuilderTest.cs` shapes a real font from
memory, which also fails the test matrix when a platform's native HarfBuzz asset
is missing: the library is resolved on first use, so a packaging mistake would
otherwise only show up on a player's machine.

Native assets come from the `HarfBuzzSharp.NativeAssets.*` packages. The managed
package carries Win32 and macOS; Linux is added by `osu.Framework.csproj`, and
Android and iOS by the platform projects, because a native asset package has to
be referenced from a project that targets that platform.

HarfBuzz is licensed under the "Old MIT" licence; see `LICENCE-HARFBUZZ`.
