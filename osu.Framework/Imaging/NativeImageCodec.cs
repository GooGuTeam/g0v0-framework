// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace osu.Framework.Imaging
{
    internal static unsafe class NativeImageCodec
    {
        private const string library = "g0v0-image-codecs";
        internal const int MAXIMUM_BYTES = 256 * 1024 * 1024;

        static NativeImageCodec()
        {
            if (g0_image_abi() != 1)
                throw new NotSupportedException("Unsupported image codec ABI.");
        }

        internal static Rgba32[] Decode(ReadOnlySpan<byte> input, out int width, out int height)
        {
            if (input.IsEmpty || input.Length > MAXIMUM_BYTES)
                throw new InvalidDataException("Image input exceeds the supported size.");
            fixed (byte* source = input)
            {
                if (g0_image_decode(source, (nuint)input.Length, out nint pixels, out width, out height) == 0)
                    throw new InvalidDataException("Wuffs could not decode the image within the resource limits.");
                try
                {
                    long size = (long)width * height * 4;
                    if (width <= 0 || height <= 0 || size > MAXIMUM_BYTES || pixels == IntPtr.Zero)
                        throw new InvalidDataException("Invalid native image dimensions.");
                    int count = Image<Rgba32>.ValidateDimensions(width, height);
                    var data = new Rgba32[count];
                    new ReadOnlySpan<Rgba32>((void*)pixels, count).CopyTo(data);
                    return data;
                }
                finally { g0_image_free(pixels); }
            }
        }

        internal static byte[] Encode(ReadOnlySpan<Rgba32> pixels, int width, int height, int format, int quality)
        {
            fixed (Rgba32* source = pixels)
            {
                if (g0_image_encode((byte*)source, checked((nuint)pixels.Length * 4), width, height,
                        format, quality, out nint output, out nuint length) == 0)
                    throw new InvalidDataException("stb_image_write could not encode the image.");
                try
                {
                    if (length > MAXIMUM_BYTES || output == IntPtr.Zero)
                        throw new InvalidDataException("Invalid encoded image size.");
                    byte[] data = new byte[checked((int)length)];
                    Marshal.Copy(output, data, 0, data.Length);
                    return data;
                }
                finally { g0_image_free(output); }
            }
        }

        internal static void Resize(ReadOnlySpan<Rgba32> source, int width, int height,
                                    Span<Rgba32> destination, int newWidth, int newHeight, ImageResampler resampler)
        {
            fixed (Rgba32* input = source)
            fixed (Rgba32* output = destination)
            {
                if (g0_image_resize((byte*)input, checked((nuint)source.Length * 4), width, height,
                        (byte*)output, checked((nuint)destination.Length * 4), newWidth, newHeight, (int)resampler) == 0)
                    throw new InvalidOperationException("Simd could not resize the image.");
            }
        }

        [DllImport(library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int g0_image_resize(byte* input, nuint inputLength, int width, int height,
                                                 byte* output, nuint outputLength, int newWidth, int newHeight, int method);

        [DllImport(library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern uint g0_image_abi();
        [DllImport(library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int g0_image_decode(byte* input, nuint length, out IntPtr pixels, out int width, out int height);
        [DllImport(library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int g0_image_encode(byte* pixels, nuint length, int width, int height, int format, int quality, out IntPtr output, out nuint outputLength);
        [DllImport(library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void g0_image_free(IntPtr pointer);
    }
}
