// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.Buffers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Textures
{
    public class MemoryAllocatorTextureUpload : ITextureUpload
    {
        public Span<Rgba32> RawData => memoryOwner.Memory.Span;

        public ReadOnlySpan<Rgba32> Data => RawData;

        private readonly IMemoryOwner<Rgba32> memoryOwner;

        public int Level { get; set; }

        public RectangleI Bounds { get; set; }

        /// <summary>
        /// Create an empty raw texture with an efficient shared memory backing.
        /// </summary>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        public MemoryAllocatorTextureUpload(int width, int height)
        {
            memoryOwner = new osu.Framework.Allocation.PooledMemoryOwner<Rgba32>(Image<Rgba32>.ValidateDimensions(width, height), clear: true);
        }

        #region IDisposable Support

        private bool disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (disposed)
                return;

            memoryOwner?.Dispose();

            disposed = true;
        }

        #endregion
    }
}
