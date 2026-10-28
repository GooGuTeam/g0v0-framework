// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Buffers;
using osu.Framework.Imaging;

namespace osu.Framework.Extensions.ImageExtensions
{
    public readonly ref struct ReadOnlyPixelSpan<TPixel>
        where TPixel : unmanaged, IPixel<TPixel>
    {
        /// <summary>
        /// The span of pixels.
        /// </summary>
        public readonly ReadOnlySpan<TPixel> Span;

        private readonly IMemoryOwner<TPixel>? owner;

        internal ReadOnlyPixelSpan(Image<TPixel> image)
        {
            if (image.DangerousTryGetSinglePixelMemory(out var memory))
            {
                owner = null;
                Span = memory.Span;
            }
            else
            {
                owner = image.CreateContiguousMemory();
                Span = owner.Memory.Span;
            }
        }

        public void Dispose()
        {
            owner?.Dispose();
        }
    }
}
