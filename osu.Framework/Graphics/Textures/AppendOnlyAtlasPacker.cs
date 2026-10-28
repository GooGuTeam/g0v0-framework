// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Primitives;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>Adapts append-only allocators to atlas padding and the permanent white pixel.</summary>
    internal abstract class AppendOnlyAtlasPacker : IAtlasPacker
    {
        private int atlasWidth;
        private int atlasHeight;
        private bool initialised;

        public void Reset(int width, int height)
        {
            atlasWidth = width;
            atlasHeight = height;
            initialised = false;
        }

        public bool TryAllocate(int width, int height, out Vector2I position)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width);
            ArgumentOutOfRangeException.ThrowIfNegative(height);
            position = default;
            const int padding = TextureAtlas.PADDING;
            const int white = TextureAtlas.WHITE_PIXEL_SIZE;
            int availableWidth = atlasWidth - 2 * padding;
            int availableHeight = atlasHeight - 2 * padding;
            if (width > availableWidth || height > availableHeight ||
                (width > availableWidth - white && height > availableHeight - white))
                return false;

            if (!initialised)
            {
                // Reserve a one-pixel strip next to the white pixel. Choose its orientation
                // on the first allocation so every size accepted by TextureAtlas still fits.
                bool belowWhite = width > availableWidth - white;
                Initialise(new RectangleI(padding + (belowWhite ? 0 : white), padding + (belowWhite ? white : 0),
                    atlasWidth - padding - (belowWhite ? 0 : white), atlasHeight - padding - (belowWhite ? white : 0)));
                initialised = true;
            }

            return Allocate(width + padding, height + padding, out position);
        }

        protected abstract void Initialise(RectangleI bounds);
        protected abstract bool Allocate(int width, int height, out Vector2I position);
    }
}
