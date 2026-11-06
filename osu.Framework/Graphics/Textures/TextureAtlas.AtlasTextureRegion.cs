// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Primitives;

namespace osu.Framework.Graphics.Textures
{
    public partial class TextureAtlas
    {
        /// <summary>
        /// An atlas allocation with a cached full-region mapping for its original backing dimensions.
        /// </summary>
        private sealed class AtlasTextureRegion : TextureRegion
        {
            private readonly BackingAtlasTexture atlasTexture;
            private readonly int atlasWidth;
            private readonly int atlasHeight;
            private readonly float atlasScaleAdjust;
            private readonly RectangleF fullTextureRect;

            public AtlasTextureRegion(BackingAtlasTexture atlasTexture, RectangleI bounds, WrapMode wrapModeS, WrapMode wrapModeT)
                : base(atlasTexture, bounds, wrapModeS, wrapModeT)
            {
                this.atlasTexture = atlasTexture;
                atlasWidth = atlasTexture.Width;
                atlasHeight = atlasTexture.Height;
                atlasScaleAdjust = atlasTexture.ScaleAdjust;

                // Preserve the region-to-parent division followed by normalisation, including floating-point rounding.
                RectangleF rect = (RectangleF)bounds / atlasScaleAdjust;
                rect.X *= atlasScaleAdjust / atlasWidth;
                rect.Y *= atlasScaleAdjust / atlasHeight;
                rect.Width *= atlasScaleAdjust / atlasWidth;
                rect.Height *= atlasScaleAdjust / atlasHeight;
                fullTextureRect = rect;
            }

            public override RectangleF GetTextureRect(RectangleF? area = null)
            {
                // Full-region lookup does not depend on this region's ScaleAdjust. Explicit areas do.
                // Keep the cache immutable: texture lookup can also run on loading/update threads.
                if (!area.HasValue
                    && atlasTexture.Width == atlasWidth
                    && atlasTexture.Height == atlasHeight
                    && atlasTexture.ScaleAdjust == atlasScaleAdjust)
                    return fullTextureRect;

                return base.GetTextureRect(area);
            }
        }
    }
}
