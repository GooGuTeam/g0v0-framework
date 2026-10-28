// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osuTK.Graphics;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Textures
{
    public partial class TextureAtlas
    {
        /// <summary>
        /// A texture which is acting as the backing for an atlas.
        /// </summary>
        private class BackingAtlasTexture : Texture
        {
            /// <summary>
            /// The amount of padding around each texture in the atlas.
            /// </summary>
            private readonly int padding;

            private readonly RectangleI atlasBounds;

#pragma warning disable IDE0052
            // Keep a reference to the parent for the texture visualiser.
            // ReSharper disable once NotAccessedField.Local
            private readonly Texture parent;
#pragma warning restore IDE0052

            private static readonly Color4 initialisation_colour = default;

            public BackingAtlasTexture(IRenderer renderer, int width, int height, bool manualMipmaps, TextureFilteringMode filteringMode = TextureFilteringMode.Linear, int padding = 0,
                                       osu.Framework.Graphics.Rendering.TexturePixelFormat textureFormat = osu.Framework.Graphics.Rendering.TexturePixelFormat.R8G8B8A8Float)
                : this(renderer.CreateTexture(width, height, manualMipmaps, filteringMode, WrapMode.None, WrapMode.None, initialisation_colour, textureFormat))
            {
                this.padding = padding;
                atlasBounds = new RectangleI(0, 0, Width, Height);
            }

            private BackingAtlasTexture(Texture parent)
                : base(parent)
            {
                this.parent = parent;
                IsAtlasTexture = parent.IsAtlasTexture = true;
            }

            internal override void SetData(ITextureUpload upload, WrapMode wrapModeS, WrapMode wrapModeT, Opacity? opacity)
            {
                if (upload is AlphaTextureUpload alpha && NativeTexture.PixelFormat == osu.Framework.Graphics.Rendering.TexturePixelFormat.R8G8B8A8Float)
                    upload = alpha.ToRgbaUpload();

                // Can only perform padding when the bounds are a sub-part of the texture
                RectangleI middleBounds = upload.Bounds;

                if (upload.Format == osu.Framework.Graphics.Rendering.TexturePixelFormat.R8Float)
                {
                    if (!middleBounds.IsEmpty)
                        uploadAlphaPadding(upload.RawData, middleBounds, padding / (1 << upload.Level), wrapModeS, wrapModeT);

                    base.SetData(upload, wrapModeS, wrapModeT, Opacity.Mixed);
                    return;
                }

                if (middleBounds.IsEmpty || (long)middleBounds.Width * middleBounds.Height * 4 > upload.RawData.Length)
                {
                    // For a texture atlas, we don't care about opacity, so we avoid
                    // any computations related to it by assuming it to be mixed.
                    base.SetData(upload, wrapModeS, wrapModeT, Opacity.Mixed);
                    return;
                }

                int actualPadding = padding / (1 << upload.Level);

                var data = upload.Data;

                uploadCornerPadding(data, middleBounds, actualPadding, wrapModeS != WrapMode.None && wrapModeT != WrapMode.None);
                uploadHorizontalPadding(data, middleBounds, actualPadding, wrapModeS != WrapMode.None);
                uploadVerticalPadding(data, middleBounds, actualPadding, wrapModeT != WrapMode.None);

                // Upload the middle part of the texture
                // For a texture atlas, we don't care about opacity, so we avoid
                // any computations related to it by assuming it to be mixed.
                base.SetData(upload, wrapModeS, wrapModeT, Opacity.Mixed);
            }

            private void uploadAlphaPadding(ReadOnlySpan<byte> upload, RectangleI bounds, int actualPadding, WrapMode wrapModeS, WrapMode wrapModeT)
            {
                if (actualPadding == 0)
                    return;

                uploadAlphaBorder(new RectangleI(bounds.X, bounds.Y - actualPadding, bounds.Width, actualPadding).Intersect(atlasBounds),
                    upload[0], wrapModeS != WrapMode.None && wrapModeT != WrapMode.None);
                uploadAlphaBorder(new RectangleI(bounds.X, bounds.Y + bounds.Height, bounds.Width, actualPadding).Intersect(atlasBounds),
                    upload[(bounds.Height - 1) * bounds.Width], wrapModeS != WrapMode.None && wrapModeT != WrapMode.None);
                uploadAlphaBorder(new RectangleI(bounds.X - actualPadding, bounds.Y, actualPadding, bounds.Height).Intersect(atlasBounds),
                    upload[0], wrapModeS != WrapMode.None);
                uploadAlphaBorder(new RectangleI(bounds.X + bounds.Width, bounds.Y, actualPadding, bounds.Height).Intersect(atlasBounds),
                    upload[bounds.Width - 1], wrapModeS != WrapMode.None);

                uploadAlphaBorder(new RectangleI(bounds.X - actualPadding, bounds.Y - actualPadding, actualPadding, actualPadding).Intersect(atlasBounds), upload[0], false);
                uploadAlphaBorder(new RectangleI(bounds.X + bounds.Width, bounds.Y - actualPadding, actualPadding, actualPadding).Intersect(atlasBounds), upload[bounds.Width - 1], false);
                uploadAlphaBorder(new RectangleI(bounds.X - actualPadding, bounds.Y + bounds.Height, actualPadding, actualPadding).Intersect(atlasBounds), upload[(bounds.Height - 1) * bounds.Width], false);
                uploadAlphaBorder(new RectangleI(bounds.X + bounds.Width, bounds.Y + bounds.Height, actualPadding, actualPadding).Intersect(atlasBounds), upload[bounds.Width * bounds.Height - 1], false);
            }

            private void uploadAlphaBorder(RectangleI bounds, byte edge, bool fillOpaque)
            {
                // Unwrapped coverage textures already have transparent padding from atlas initialisation.
                if (bounds.IsEmpty || !fillOpaque || edge == 0)
                    return;

                byte[] pixels = new byte[checked(bounds.Width * bounds.Height)];
                Array.Fill(pixels, edge);

                var borderUpload = new AlphaTextureUpload(bounds.Width, bounds.Height, pixels) { Bounds = bounds };
                base.SetData(borderUpload, WrapMode.None, WrapMode.None, Opacity.Mixed);
            }

            private void uploadVerticalPadding(ReadOnlySpan<Rgba32> upload, RectangleI middleBounds, int actualPadding, bool fillOpaque)
            {
                RectangleI[] sideBoundsArray =
                {
                    new RectangleI(middleBounds.X, middleBounds.Y - actualPadding, middleBounds.Width, actualPadding).Intersect(atlasBounds), // Top
                    new RectangleI(middleBounds.X, middleBounds.Y + middleBounds.Height, middleBounds.Width, actualPadding).Intersect(atlasBounds), // Bottom
                };

                int[] sideIndices =
                {
                    0, // Top
                    (middleBounds.Height - 1) * middleBounds.Width, // Bottom
                };

                for (int i = 0; i < 2; ++i)
                {
                    RectangleI sideBounds = sideBoundsArray[i];

                    if (!sideBounds.IsEmpty)
                    {
                        bool allTransparent = true;
                        int index = sideIndices[i];

                        var sideUpload = new MemoryAllocatorTextureUpload(sideBounds.Width, sideBounds.Height) { Bounds = sideBounds };
                        var data = sideUpload.RawData;

                        for (int y = 0; y < sideBounds.Height; ++y)
                        {
                            for (int x = 0; x < sideBounds.Width; ++x)
                            {
                                Rgba32 pixel = upload[index + x];
                                allTransparent &= checkEdgeRGB(pixel);

                                transferBorderPixel(ref data[y * sideBounds.Width + x], pixel, fillOpaque);
                            }
                        }

                        // Only upload padding if the border isn't completely transparent.
                        if (!allTransparent)
                        {
                            // For a texture atlas, we don't care about opacity, so we avoid
                            // any computations related to it by assuming it to be mixed.
                            base.SetData(sideUpload, WrapMode.None, WrapMode.None, Opacity.Mixed);
                        }
                    }
                }
            }

            private void uploadHorizontalPadding(ReadOnlySpan<Rgba32> upload, RectangleI middleBounds, int actualPadding, bool fillOpaque)
            {
                RectangleI[] sideBoundsArray =
                {
                    new RectangleI(middleBounds.X - actualPadding, middleBounds.Y, actualPadding, middleBounds.Height).Intersect(atlasBounds), // Left
                    new RectangleI(middleBounds.X + middleBounds.Width, middleBounds.Y, actualPadding, middleBounds.Height).Intersect(atlasBounds), // Right
                };

                int[] sideIndices =
                {
                    0, // Left
                    middleBounds.Width - 1, // Right
                };

                for (int i = 0; i < 2; ++i)
                {
                    RectangleI sideBounds = sideBoundsArray[i];

                    if (!sideBounds.IsEmpty)
                    {
                        bool allTransparent = true;
                        int index = sideIndices[i];

                        var sideUpload = new MemoryAllocatorTextureUpload(sideBounds.Width, sideBounds.Height) { Bounds = sideBounds };
                        var data = sideUpload.RawData;

                        int stride = middleBounds.Width;

                        for (int y = 0; y < sideBounds.Height; ++y)
                        {
                            for (int x = 0; x < sideBounds.Width; ++x)
                            {
                                Rgba32 pixel = upload[index + y * stride];

                                allTransparent &= checkEdgeRGB(pixel);

                                transferBorderPixel(ref data[y * sideBounds.Width + x], pixel, fillOpaque);
                            }
                        }

                        // Only upload padding if the border isn't completely transparent.
                        if (!allTransparent)
                        {
                            // For a texture atlas, we don't care about opacity, so we avoid
                            // any computations related to it by assuming it to be mixed.
                            base.SetData(sideUpload, WrapMode.None, WrapMode.None, Opacity.Mixed);
                        }
                    }
                }
            }

            private void uploadCornerPadding(ReadOnlySpan<Rgba32> upload, RectangleI middleBounds, int actualPadding, bool fillOpaque)
            {
                RectangleI[] cornerBoundsArray =
                {
                    new RectangleI(middleBounds.X - actualPadding, middleBounds.Y - actualPadding, actualPadding, actualPadding).Intersect(atlasBounds), // TopLeft
                    new RectangleI(middleBounds.X + middleBounds.Width, middleBounds.Y - actualPadding, actualPadding, actualPadding).Intersect(atlasBounds), // TopRight
                    new RectangleI(middleBounds.X - actualPadding, middleBounds.Y + middleBounds.Height, actualPadding, actualPadding).Intersect(atlasBounds), // BottomLeft
                    new RectangleI(middleBounds.X + middleBounds.Width, middleBounds.Y + middleBounds.Height, actualPadding, actualPadding).Intersect(atlasBounds), // BottomRight
                };

                int[] cornerIndices =
                {
                    0, // TopLeft
                    middleBounds.Width - 1, // TopRight
                    (middleBounds.Height - 1) * middleBounds.Width, // BottomLeft
                    (middleBounds.Height - 1) * middleBounds.Width + middleBounds.Width - 1, // BottomRight
                };

                for (int i = 0; i < 4; ++i)
                {
                    RectangleI cornerBounds = cornerBoundsArray[i];
                    int nCornerPixels = cornerBounds.Width * cornerBounds.Height;
                    Rgba32 pixel = upload[cornerIndices[i]];

                    // Only upload if we have a non-zero size and if the colour isn't already transparent white
                    if (nCornerPixels > 0 && !checkEdgeRGB(pixel))
                    {
                        var cornerUpload = new MemoryAllocatorTextureUpload(cornerBounds.Width, cornerBounds.Height) { Bounds = cornerBounds };
                        var data = cornerUpload.RawData;

                        for (int j = 0; j < nCornerPixels; ++j)
                            transferBorderPixel(ref data[j], pixel, fillOpaque);

                        // For a texture atlas, we don't care about opacity, so we avoid
                        // any computations related to it by assuming it to be mixed.
                        base.SetData(cornerUpload, WrapMode.None, WrapMode.None, Opacity.Mixed);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void transferBorderPixel(ref Rgba32 dest, Rgba32 source, bool fillOpaque)
            {
                dest.R = source.R;
                dest.G = source.G;
                dest.B = source.B;
                dest.A = fillOpaque ? source.A : (byte)0;
            }

            /// <summary>
            /// Check whether the provided upload edge pixel's RGB components match the initialisation colour.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool checkEdgeRGB(Rgba32 cornerPixel)
                => cornerPixel.R == initialisation_colour.R
                   && cornerPixel.G == initialisation_colour.G
                   && cornerPixel.B == initialisation_colour.B;
        }
    }
}
