// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// FreeTypeSharp's iOS libfreetype.a is built with PNG support (FT_CONFIG_OPTION_USE_PNG) but does not bundle libpng,
// and iOS has no system libpng, so apps fail to link. FreeType only decodes PNG for embedded colour bitmap glyphs
// (sbix/CBDT strikes), which TrueTypeGlyphStore never loads (FT_LOAD_NO_BITMAP).
//
// png_create_read_struct returns NULL, which FreeType's Load_SBit_Png already handles by failing that one glyph
// with FT_Err_Out_Of_Memory. Nothing else is reachable without a read struct, so the rest trap if ever called.

void *png_create_read_struct(const char *version, void *error_ptr, void *error_fn, void *warn_fn)
{
    (void)version;
    (void)error_ptr;
    (void)error_fn;
    (void)warn_fn;
    return 0;
}

#define UNREACHABLE(name)     void name(void);          void name(void) { __builtin_trap(); }

UNREACHABLE(png_create_info_struct)
UNREACHABLE(png_destroy_read_struct)
UNREACHABLE(png_error)
UNREACHABLE(png_get_IHDR)
UNREACHABLE(png_get_error_ptr)
UNREACHABLE(png_get_io_ptr)
UNREACHABLE(png_get_valid)
UNREACHABLE(png_read_end)
UNREACHABLE(png_read_image)
UNREACHABLE(png_read_info)
UNREACHABLE(png_read_update_info)
UNREACHABLE(png_set_expand_gray_1_2_4_to_8)
UNREACHABLE(png_set_filler)
UNREACHABLE(png_set_gray_to_rgb)
UNREACHABLE(png_set_interlace_handling)
UNREACHABLE(png_set_longjmp_fn)
UNREACHABLE(png_set_packing)
UNREACHABLE(png_set_palette_to_rgb)
UNREACHABLE(png_set_read_fn)
UNREACHABLE(png_set_read_user_transform_fn)
UNREACHABLE(png_set_strip_16)
UNREACHABLE(png_set_tRNS_to_alpha)
