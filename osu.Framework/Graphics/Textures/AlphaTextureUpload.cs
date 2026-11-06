// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>A single-channel 8-bit coverage upload, used for monochrome font glyphs.</summary>
    internal sealed class AlphaTextureUpload : TextureUpload
    {
        private readonly byte[] pixels;

        public override int Width { get; }
        public override int Height { get; }
        public override TexturePixelFormat Format => TexturePixelFormat.R8Float;
        public override ReadOnlySpan<byte> RawData => pixels.AsSpan(0, Width * Height);
        public override ReadOnlySpan<Rgba32> Data => ReadOnlySpan<Rgba32>.Empty;

        public AlphaTextureUpload(int width, int height, byte[] pixels)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            if (pixels.Length < Image<Rgba32>.ValidateDimensions(width, height))
                throw new ArgumentException("Coverage data is smaller than the requested image.", nameof(pixels));

            Width = width;
            Height = height;
            this.pixels = pixels;
        }

        public TextureUpload ToRgbaUpload()
        {
            var image = new Image<Rgba32>(Width, Height);
            var destination = image.Pixels;

            for (int i = 0; i < destination.Length; i++)
                destination[i] = new Rgba32(255, 255, 255, pixels[i]);

            return new TextureUpload(image)
            {
                Bounds = Bounds,
                Level = Level
            };
        }
    }
}
