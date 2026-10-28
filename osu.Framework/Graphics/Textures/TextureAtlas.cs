// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Textures
{
    public partial class TextureAtlas
    {
        // We are adding an extra padding on top of the padding required by
        // mipmap blending in order to support smooth edges without antialiasing which requires
        // inflating texture rectangles.
        internal const int PADDING = (1 << IRenderer.MAX_MIPMAP_LEVELS) * Sprite.MAX_EDGE_SMOOTHNESS;
        internal const int WHITE_PIXEL_SIZE = 1;

        private readonly List<RectangleI> subTextureBounds = new List<RectangleI>();
        private IAtlasPacker? freeRectanglePacker;
        private readonly AtlasPackingStrategy packingStrategy;
        private readonly List<(WeakReference<BackingAtlasTexture> Texture, IAtlasPacker Packer)> previousPages = new();
        private BackingAtlasTexture? atlasTexture;

        private readonly IRenderer renderer;
        private readonly int atlasWidth;
        private readonly int atlasHeight;

        private int maxFittableWidth => atlasWidth - PADDING * 2;
        private int maxFittableHeight => atlasHeight - PADDING * 2;

        private Vector2I currentPosition;

        internal TextureWhitePixel WhitePixel
        {
            get
            {
                if (atlasTexture == null)
                    Reset();

                Debug.Assert(atlasTexture != null, "Atlas texture should not be null after Reset().");

                return new TextureWhitePixel(atlasTexture);
            }
        }

        private readonly bool manualMipmaps;
        private readonly TextureFilteringMode filteringMode;
        private readonly osu.Framework.Graphics.Rendering.TexturePixelFormat textureFormat;

        public osu.Framework.Graphics.Rendering.TexturePixelFormat TextureFormat => textureFormat;
        private readonly Lock textureRetrievalLock = new Lock();

        public TextureAtlas(IRenderer renderer, int width, int height, bool manualMipmaps = false, TextureFilteringMode filteringMode = TextureFilteringMode.Linear)
            : this(renderer, width, height, (renderer as Renderer)?.TextureAtlasPacking ?? AtlasPackingStrategy.LegacyRows, manualMipmaps, filteringMode)
        {
        }

        internal TextureAtlas(IRenderer renderer, int width, int height, bool manualMipmaps, TextureFilteringMode filteringMode, bool useFreeRectanglePacking)
            : this(renderer, width, height, useFreeRectanglePacking ? AtlasPackingStrategy.FreeRectangles : AtlasPackingStrategy.LegacyRows, manualMipmaps, filteringMode)
        {
        }

        /// <summary>Creates an atlas with an explicit allocation policy, without relocating existing regions.</summary>
        public TextureAtlas(IRenderer renderer, int width, int height, AtlasPackingStrategy packingStrategy, bool manualMipmaps = false,
                            TextureFilteringMode filteringMode = TextureFilteringMode.Linear,
                            osu.Framework.Graphics.Rendering.TexturePixelFormat textureFormat = osu.Framework.Graphics.Rendering.TexturePixelFormat.R8G8B8A8Float)
        {
            this.renderer = renderer;
            atlasWidth = width;
            atlasHeight = height;
            this.manualMipmaps = manualMipmaps;
            this.filteringMode = filteringMode;
            this.textureFormat = textureFormat;
            this.packingStrategy = packingStrategy;

            freeRectanglePacker = createPacker();
        }

        private IAtlasPacker? createPacker() => packingStrategy switch
        {
            AtlasPackingStrategy.LegacyRows => null,
            AtlasPackingStrategy.FreeRectangles => new FreeRectanglePacker(),
            AtlasPackingStrategy.Etagere => new EtagereAtlasPacker(),
            AtlasPackingStrategy.Guillotiere => new GuillotiereAtlasPacker(),
            _ => throw new ArgumentOutOfRangeException(nameof(packingStrategy)),
        };

        private int exceedCount;

        /// <summary>
        /// Creates a new empty texture.
        /// </summary>
        /// <remarks>
        /// Existing textures created via <see cref="Add"/> are not cleared and remain accessible by usages.
        /// </remarks>
        [MemberNotNull(nameof(atlasTexture))]
        public void Reset()
        {
            lock (textureRetrievalLock)
            {
                previousPages.Clear();
                resetCurrentPage();
            }
        }

        [MemberNotNull(nameof(atlasTexture))]
        private void resetCurrentPage()
        {
            subTextureBounds.Clear();
            currentPosition = Vector2I.Zero;

            // We pass PADDING/2 as opposed to PADDING such that the padded region of each individual texture
            // occupies half of the padded space.
            atlasTexture = new BackingAtlasTexture(renderer, atlasWidth, atlasHeight, manualMipmaps, filteringMode,
                textureFormat == osu.Framework.Graphics.Rendering.TexturePixelFormat.R8Float ? 0 : PADDING / 2, textureFormat);

            RectangleI bounds = new RectangleI(0, 0, WHITE_PIXEL_SIZE, WHITE_PIXEL_SIZE);
            if (freeRectanglePacker == null)
                subTextureBounds.Add(bounds);

            using (var whiteTex = new TextureRegion(atlasTexture, bounds, WrapMode.Repeat, WrapMode.Repeat))
            {
                if (textureFormat == osu.Framework.Graphics.Rendering.TexturePixelFormat.R8Float)
                    whiteTex.SetData(new AlphaTextureUpload(1, 1, new byte[] { 255 }));
                else
                    // Generate white padding as if the white texture was wrapped, even though it isn't
                    whiteTex.SetData(new TextureUpload(new Image<Rgba32>(whiteTex.Width, whiteTex.Height, new Rgba32(Vector4.One))));
            }

            currentPosition = new Vector2I(PADDING + WHITE_PIXEL_SIZE, PADDING);
            freeRectanglePacker = createPacker();
            freeRectanglePacker?.Reset(atlasWidth, atlasHeight);
        }

        /// <summary>
        /// Add (allocate) a new texture in the atlas.
        /// </summary>
        /// <param name="width">The width of the requested texture.</param>
        /// <param name="height">The height of the requested texture.</param>
        /// <param name="wrapModeS">The horizontal wrap mode of the texture.</param>
        /// <param name="wrapModeT">The vertical wrap mode of the texture.</param>
        /// <returns>A texture, or null if the requested size exceeds the atlas' bounds.</returns>
        public Texture? Add(int width, int height, WrapMode wrapModeS = WrapMode.None, WrapMode wrapModeT = WrapMode.None)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width);
            ArgumentOutOfRangeException.ThrowIfNegative(height);
            if (!canFitEmptyTextureAtlas(width, height))
                return null;

            lock (textureRetrievalLock)
            {
                Vector2I position = findPosition(width, height, out BackingAtlasTexture page);

                RectangleI bounds = new RectangleI(position.X, position.Y, width, height);
                if (freeRectanglePacker == null)
                    subTextureBounds.Add(bounds);

                return new AtlasTextureRegion(page, bounds, wrapModeS, wrapModeT);
            }
        }

        /// <summary>
        /// Whether or not a texture of the given width and height could be placed into a completely empty texture atlas
        /// </summary>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        /// <returns>True if the texture could fit an empty texture atlas, false if it could not</returns>
        private bool canFitEmptyTextureAtlas(int width, int height)
        {
            // exceeds bounds in one direction
            if (width > maxFittableWidth || height > maxFittableHeight)
                return false;

            // exceeds bounds in both directions (in this one, we have to account for the white pixel)
            if (width + WHITE_PIXEL_SIZE > maxFittableWidth && height + WHITE_PIXEL_SIZE > maxFittableHeight)
                return false;

            return true;
        }

        /// <summary>
        /// Locates a position in the current texture atlas for a new texture of the given size, or
        /// creates a new texture atlas if there is not enough space in the current one.
        /// </summary>
        /// <param name="width">The width of the requested texture.</param>
        /// <param name="height">The height of the requested texture.</param>
        /// <param name="page">The backing page containing the allocated region.</param>
        /// <returns>The position within the texture atlas to place the new texture.</returns>
        private Vector2I findPosition(int width, int height, out BackingAtlasTexture page)
        {
            if (atlasTexture == null)
            {
                Logger.Log($"TextureAtlas initialised ({atlasWidth}x{atlasHeight})", LoggingTarget.Performance);
                Reset();
            }

            Debug.Assert(atlasTexture != null);
            page = atlasTexture;

            if (freeRectanglePacker != null)
            {
                if (!freeRectanglePacker.TryAllocate(width, height, out Vector2I position))
                {
                    // Keep consecutive loads together when possible, then reuse holes in older live pages.
                    // Weak references allow unused backing textures to be collected with their regions.
                    for (int i = previousPages.Count - 1; i >= 0; i--)
                    {
                        var previous = previousPages[i];
                        if (!previous.Texture.TryGetTarget(out var candidate) || !candidate.Available)
                        {
                            previousPages.RemoveAt(i);
                            continue;
                        }

                        if (previous.Packer.TryAllocate(width, height, out position))
                        {
                            page = candidate;
                            return position;
                        }
                    }

                    Logger.Log($"TextureAtlas size exceeded {++exceedCount} time(s); generating new texture ({atlasWidth}x{atlasHeight})", LoggingTarget.Performance);
                    previousPages.Add((new WeakReference<BackingAtlasTexture>(atlasTexture), freeRectanglePacker));
                    resetCurrentPage();
                    page = atlasTexture;

                    // Add has already checked that the allocation fits an empty page, including its white pixel.
                    if (!freeRectanglePacker!.TryAllocate(width, height, out position))
                        throw new InvalidOperationException(@"A validated texture allocation could not fit an empty atlas.");
                }

                return position;
            }

            if (currentPosition.Y + height + PADDING > atlasHeight)
            {
                Logger.Log($"TextureAtlas size exceeded {++exceedCount} time(s); generating new texture ({atlasWidth}x{atlasHeight})", LoggingTarget.Performance);
                Reset();
                page = atlasTexture;
            }

            if (currentPosition.X + width + PADDING > atlasWidth)
            {
                int maxY = 0;

                foreach (RectangleI bounds in subTextureBounds)
                    maxY = Math.Max(maxY, bounds.Bottom + PADDING);

                subTextureBounds.Clear();
                currentPosition = new Vector2I(PADDING, maxY);

                return findPosition(width, height, out page);
            }

            var result = currentPosition;
            currentPosition.X += width + PADDING;

            return result;
        }
    }
}
