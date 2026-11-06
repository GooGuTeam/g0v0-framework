// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.Buffers;
using System.Threading;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Textures
{
    public class ArrayPoolTextureUpload : ITextureUpload
    {
        private readonly ArrayPool<Rgba32> arrayPool;

        private Rgba32[] data;
        private readonly int length;

        /// <summary>
        /// Create an empty raw texture with an efficient shared memory backing.
        /// </summary>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        /// <param name="arrayPool">The source pool to retrieve memory from. Shared default is used if null.</param>
        public ArrayPoolTextureUpload(int width, int height, ArrayPool<Rgba32> arrayPool = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width);
            ArgumentOutOfRangeException.ThrowIfNegative(height);
            length = checked(width * height);
            this.arrayPool = arrayPool ?? ArrayPool<Rgba32>.Shared;
            data = this.arrayPool.Rent(length);
        }

        public void Dispose()
        {
            var rented = Interlocked.Exchange(ref data, null);
            if (rented != null)
                arrayPool.Return(rented);
        }

        public Span<Rgba32> RawData
        {
            get
            {
                var rented = data;
                ObjectDisposedException.ThrowIf(rented == null, this);
                return rented.AsSpan(0, length);
            }
        }

        public ReadOnlySpan<Rgba32> Data => RawData;

        public int Level { get; set; }

        public RectangleI Bounds { get; set; }
    }
}
