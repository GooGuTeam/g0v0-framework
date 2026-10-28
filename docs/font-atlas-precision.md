# Font atlas precision and resolution

Font glyphs are coverage masks, so the font atlas stores one 8-bit coverage value per texel (`R8_UNorm`) instead of four RGBA channels. Glyph uploads stay single-channel through atlas padding, texture allocation, and GPU upload. The text fragment shader reads the red channel as coverage; ordinary image textures keep their RGBA shader and format.

Set `FrameworkSetting.LowResolutionFonts` to `true` to halve glyph texture width and height. The loader averages each 2×2 coverage block, while `TexturedCharacterGlyph` scales the smaller texture back to the original font metrics. This reduces each glyph's pixel and upload data to roughly one quarter and lets more glyphs fit in a page. The setting is read while the host creates its font stores and should be changed before startup. Atlas page dimensions remain fixed, so the setting reduces GPU memory only when it avoids allocating additional pages.

`FrameworkSetting.TextureAtlasPacking` defaults to `Guillotiere` for general texture atlases, including skin and icon textures. `FrameworkSetting.FontAtlasPacking` defaults to `Etagere` for the small, frequently allocated glyph rectangles. Both can be changed to compare against `LegacyRows` or `FreeRectangles`.

Both allocators now retain the free-space state of older live pages and try those pages before creating another page. See [atlas page reuse and Vulkan descriptor arrays](atlas-pages-and-descriptor-arrays.md) for allocation lifetime and renderer integration status.

Single-channel glyph atlases are intended for monochrome bitmap font coverage. The current `GlyphStore` path treats the source alpha channel as coverage, matching the bitmap font contract.
