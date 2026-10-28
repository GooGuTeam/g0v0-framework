// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System;
using System.Buffers;
using System.Drawing;

namespace osu.Framework.Imaging
{
    public sealed class ImageProcessingContext<T> where T : unmanaged, IPixel<T>
    {
        private readonly Image<T> image;
        internal ImageProcessingContext(Image<T> image) => this.image = image;
        public Size GetCurrentSize() => image.Size;
        public ImageProcessingContext<T> Crop(int width, int height) => Crop(new Rectangle(0, 0, width, height));
        public ImageProcessingContext<T> Crop(Rectangle rectangle)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0 || rectangle.X < 0 || rectangle.Y < 0 ||
                (long)rectangle.X + rectangle.Width > image.Width || (long)rectangle.Y + rectangle.Height > image.Height)
                throw new ArgumentOutOfRangeException(nameof(rectangle));
            var pixels = new T[checked(rectangle.Width * rectangle.Height)];
            for (int y = 0; y < rectangle.Height; y++)
                image.Pixels.Slice((y + rectangle.Y) * image.Width + rectangle.X, rectangle.Width).CopyTo(pixels.AsSpan(y * rectangle.Width));
            image.ReplacePixels(rectangle.Width, rectangle.Height, pixels);
            return this;
        }

        public ImageProcessingContext<T> FlipVertical()
        {
            if (image.Height < 2)
                return this;
            var temporary = ArrayPool<T>.Shared.Rent(image.Width);
            try
            {
                var pixels = image.Pixels;
                var row = temporary.AsSpan(0, image.Width);
                for (int y = 0; y < image.Height / 2; y++)
                {
                    var top = pixels.Slice(y * image.Width, image.Width);
                    var bottom = pixels.Slice((image.Height - y - 1) * image.Width, image.Width);
                    top.CopyTo(row);
                    bottom.CopyTo(top);
                    row.CopyTo(bottom);
                }
            }
            finally { ArrayPool<T>.Shared.Return(temporary); }
            return this;
        }

        public ImageProcessingContext<T> Grayscale()
        {
            for (int i = 0; i < image.Pixels.Length; i++)
            {
                var p = image.Pixels[i].ToRgba32();
                byte luma = (byte)Math.Clamp((int)MathF.Round(p.R * 0.299f + p.G * 0.587f + p.B * 0.114f), 0, 255);
                T value = default;
                value.FromRgba32(new Rgba32(luma, luma, luma, p.A));
                image.Pixels[i] = value;
            }
            return this;
        }

        public ImageProcessingContext<T> Resize(Size size, ImageResampler resampler = ImageResampler.Bilinear)
        {
            if (resampler != ImageResampler.Bilinear && resampler != ImageResampler.Bicubic)
                throw new ArgumentOutOfRangeException(nameof(resampler));
            int pixelCount = Image<T>.ValidateDimensions(size.Width, size.Height);

            if (size == image.Size)
                return this;

            if (typeof(T) == typeof(Rgba32))
            {
                var result = new T[pixelCount];
                NativeImageCodec.Resize(System.Runtime.InteropServices.MemoryMarshal.Cast<T, Rgba32>(image.Pixels),
                    image.Width, image.Height, System.Runtime.InteropServices.MemoryMarshal.Cast<T, Rgba32>(result.AsSpan()),
                    size.Width, size.Height, resampler);
                image.ReplacePixels(size.Width, size.Height, result);
            }
            else
            {
                using var rgba = image.CloneAs<Rgba32>();
                rgba.Mutate(context => context.Resize(size, resampler));
                using var converted = rgba.CloneAs<T>();
                image.ReplacePixels(size.Width, size.Height, converted.Pixels.ToArray());
            }
            return this;
        }

    }
}
