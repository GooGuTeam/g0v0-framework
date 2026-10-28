// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace osu.Framework.Imaging
{
    public abstract class Image : IDisposable
    {
        public abstract int Width { get; }
        public abstract int Height { get; }
        public System.Drawing.Size Size => new System.Drawing.Size(Width, Height);
        public abstract Image<T> CloneAs<T>() where T : unmanaged, IPixel<T>;
        public abstract void Dispose();

        public static Image<T> Load<T>(Stream stream) where T : unmanaged, IPixel<T>
        {
            // Texture stores mostly pass whole files in memory; decode those in place.
            if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> segment))
                return Load<T>(segment.AsSpan((int)memory.Position));

            long remaining = stream.CanSeek ? stream.Length - stream.Position : 0;
            if (remaining > NativeImageCodec.MAXIMUM_BYTES)
                throw new InvalidDataException("Image input exceeds the supported size.");

            // Sized up front so that large images are not copied while the buffer grows.
            using var buffer = new MemoryStream((int)remaining);
            byte[] chunk = new byte[65536];
            int read;
            while ((read = stream.Read(chunk)) != 0)
            {
                if (buffer.Length + read > NativeImageCodec.MAXIMUM_BYTES)
                    throw new InvalidDataException("Image input exceeds the supported size.");
                buffer.Write(chunk, 0, read);
            }
            return Load<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        public static Image<T> Load<T>(ReadOnlySpan<byte> source) where T : unmanaged, IPixel<T>
        {
            var pixels = NativeImageCodec.Decode(source, out int width, out int height);
            var rgba = Image<Rgba32>.TakeOwnership(width, height, pixels);
            if (typeof(T) == typeof(Rgba32))
                return (Image<T>)(object)rgba;
            using (rgba)
                return rgba.CloneAs<T>();
        }

        public static Task<Image> LoadAsync(string path) => Task.Run<Image>(() =>
        {
            using var stream = File.OpenRead(path);
            return Load<Rgba32>(stream);
        });

        public static Image<T> LoadPixelData<T>(Span<T> data, int width, int height) where T : unmanaged, IPixel<T>
            => LoadPixelData<T>((ReadOnlySpan<T>)data, width, height);

        public static Image<T> LoadPixelData<T>(ReadOnlySpan<T> data, int width, int height) where T : unmanaged, IPixel<T>
        {
            var result = new Image<T>(width, height);
            try { data.Slice(0, checked(width * height)).CopyTo(result.Pixels); return result; }
            catch { result.Dispose(); throw; }
        }

        public static Image<T> LoadPixelData<T>(ReadOnlySpan<byte> data, int width, int height) where T : unmanaged, IPixel<T>
            => LoadPixelData<T>(MemoryMarshal.Cast<byte, T>(data), width, height);

        private byte[] encode(int format, int quality)
        {
            if (this is Image<Rgba32> direct)
                return NativeImageCodec.Encode(direct.Pixels, Width, Height, format, quality);
            using var rgba = CloneAs<Rgba32>();
            return NativeImageCodec.Encode(rgba.Pixels, Width, Height, format, quality);
        }

        public void SaveAsPng(Stream stream) => stream.Write(encode(0, 90));
        public void SaveAsBmp(Stream stream) => stream.Write(encode(2, 90));
        public Task SaveAsPngAsync(Stream stream) => stream.WriteAsync(encode(0, 90)).AsTask();
        public Task SaveAsJpegAsync(Stream stream, int quality = 90) => stream.WriteAsync(encode(1, quality)).AsTask();
    }

    public sealed class Image<T> : Image where T : unmanaged, IPixel<T>
    {
        private T[]? pixels;
        private int width;
        private int height;
        public override int Width => width;
        public override int Height => height;
        public Span<T> Pixels
        {
            get { ObjectDisposedException.ThrowIf(pixels == null, this); return pixels; }
        }

        public Image(int width, int height) : this(width, height, new T[ValidateDimensions(width, height)]) { }
        public Image(int width, int height, T colour) : this(width, height)
        {
            Pixels.Fill(colour);
        }

        private Image(int width, int height, T[] ownedPixels)
        {
            this.width = width;
            this.height = height;
            pixels = ownedPixels;
        }

        internal static Image<T> TakeOwnership(int width, int height, T[] ownedPixels)
        {
            if (ownedPixels.Length != ValidateDimensions(width, height))
                throw new ArgumentException("Pixel buffer does not match image dimensions.", nameof(ownedPixels));
            return new Image<T>(width, height, ownedPixels);
        }

        internal static int ValidateDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height * Marshal.SizeOf<T>() > NativeImageCodec.MAXIMUM_BYTES)
                throw new ArgumentOutOfRangeException(nameof(width), "Image exceeds the pixel budget.");
            return checked(width * height);
        }

        public T this[int x, int y]
        {
            get { checkCoordinates(x, y); return Pixels[y * Width + x]; }
            set { checkCoordinates(x, y); Pixels[y * Width + x] = value; }
        }

        private void checkCoordinates(int x, int y)
        {
            if ((uint)x >= Width || (uint)y >= Height) throw new ArgumentOutOfRangeException(nameof(x));
        }

        public Memory<T> DangerousGetPixelRowMemory(int row)
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            if ((uint)row >= Height) throw new ArgumentOutOfRangeException(nameof(row));
            return pixels.AsMemory(row * Width, Width);
        }

        public bool DangerousTryGetSinglePixelMemory(out Memory<T> memory)
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            memory = pixels.AsMemory(); return true;
        }

        public Image<T> Clone() => CloneAs<T>();

        public override Image<TPixel> CloneAs<TPixel>()
        {
            var result = new Image<TPixel>(Width, Height);
            var source = Pixels;
            var destination = result.Pixels;
            if (typeof(T) == typeof(TPixel))
            {
                MemoryMarshal.Cast<T, TPixel>(source).CopyTo(destination);
                return result;
            }
            for (int i = 0; i < source.Length; i++)
            {
                TPixel pixel = default;
                pixel.FromRgba32(source[i].ToRgba32());
                destination[i] = pixel;
            }
            return result;
        }

        internal void ReplacePixels(int newWidth, int newHeight, T[] replacement)
        {
            ObjectDisposedException.ThrowIf(pixels == null, this);
            if (replacement.Length != ValidateDimensions(newWidth, newHeight))
                throw new ArgumentException("Pixel buffer does not match image dimensions.", nameof(replacement));
            pixels = replacement; width = newWidth; height = newHeight;
        }

        public void Mutate(Action<ImageProcessingContext<T>> action) => action(new ImageProcessingContext<T>(this));
        public override void Dispose() => pixels = null;
    }
}
