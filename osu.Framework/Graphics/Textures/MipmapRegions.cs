// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>
    /// Plans regional regeneration of automatic mipmap levels 1 to <see cref="IRenderer.MAX_MIPMAP_LEVELS"/>.
    /// <para>
    /// Every texel of level <c>k</c> is a 2x2 average of level <c>k - 1</c>, so it depends only on a
    /// <c>2^k</c>-texel square block of level 0. Regions aligned to <see cref="ALIGNMENT"/> therefore contain everything
    /// needed for the levels the renderer samples, and mipmaps generated for an aligned region alone match those
    /// generated for the whole texture, provided the texture's dimensions are also aligned.
    /// </para>
    /// </summary>
    internal static class MipmapRegions
    {
        public const int ALIGNMENT = 1 << IRenderer.MAX_MIPMAP_LEVELS;

        /// <summary>
        /// Regional generation is used only while the aligned regions cover at most this fraction of the texture.
        /// Beyond it, a single whole-texture generation is cheaper than many separate ones.
        /// </summary>
        private const int max_area_divisor = 4;

        private const int max_regions = 16;

        /// <summary>
        /// Whether regional regeneration may be used. Disabling it restores whole-texture generation, for comparison.
        /// </summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>
        /// Aligns and merges level-0 upload regions.
        /// </summary>
        /// <param name="uploads">The uploaded level-0 regions. Contents are consumed.</param>
        /// <param name="width">The texture width.</param>
        /// <param name="height">The texture height.</param>
        /// <param name="regions">The aligned, non-overlapping regions to regenerate.</param>
        /// <returns>Whether regional regeneration should be used. If false, regenerate the whole texture.</returns>
        public static bool TryPlan(List<RectangleI> uploads, int width, int height, List<RectangleI> regions)
        {
            regions.Clear();

            if (!Enabled || uploads.Count == 0 || width % ALIGNMENT != 0 || height % ALIGNMENT != 0)
                return false;

            var bounds = new RectangleI(0, 0, width, height);

            foreach (var upload in uploads)
            {
                if (upload.IsEmpty)
                    continue;

                var clamped = upload.Intersect(bounds);

                // An upload outside the texture is unexpected; leave it to the existing whole-texture path.
                if (clamped != upload)
                    return false;

                regions.Add(align(upload));
            }

            if (regions.Count == 0)
                return false;

            // Repeatedly merge overlapping or touching regions until none remain. Merging touching regions keeps the
            // number of separate generations low for adjacent uploads, such as a glyph and its padding.
            bool merged;

            do
            {
                merged = false;

                for (int i = 0; i < regions.Count; i++)
                {
                    for (int j = regions.Count - 1; j > i; j--)
                    {
                        if (!touches(regions[i], regions[j]))
                            continue;

                        regions[i] = RectangleI.Union(regions[i], regions[j]);
                        regions.RemoveAt(j);
                        merged = true;
                    }
                }
            } while (merged);

            long area = 0;

            foreach (var region in regions)
                area += (long)region.Width * region.Height;

            if (regions.Count > max_regions || area * max_area_divisor > (long)width * height)
            {
                regions.Clear();
                return false;
            }

            return true;
        }

        private static RectangleI align(RectangleI rect)
        {
            int left = rect.Left / ALIGNMENT * ALIGNMENT;
            int top = rect.Top / ALIGNMENT * ALIGNMENT;
            int right = (rect.Right + ALIGNMENT - 1) / ALIGNMENT * ALIGNMENT;
            int bottom = (rect.Bottom + ALIGNMENT - 1) / ALIGNMENT * ALIGNMENT;
            return new RectangleI(left, top, right - left, bottom - top);
        }

        private static bool touches(RectangleI a, RectangleI b)
            => a.Left <= b.Right && b.Left <= a.Right && a.Top <= b.Bottom && b.Top <= a.Bottom;

        /// <summary>
        /// Whether a texture format is supported by regional regeneration.
        /// </summary>
        public static bool SupportsFormat(TexturePixelFormat format)
            => format == TexturePixelFormat.R8G8B8A8Float || format == TexturePixelFormat.R8Float;

        internal static void ThrowIfMisaligned(RectangleI region)
        {
            if (region.X % ALIGNMENT != 0 || region.Y % ALIGNMENT != 0 || region.Width % ALIGNMENT != 0 || region.Height % ALIGNMENT != 0 || region.IsEmpty)
                throw new ArgumentException($"Mipmap regions must be non-empty and aligned to {ALIGNMENT} texels.", nameof(region));
        }
    }
}
