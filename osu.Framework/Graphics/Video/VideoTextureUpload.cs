// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Video
{
    public sealed class VideoTextureUpload : ITextureUpload
    {
        public int GetPlaneWidth(uint plane)
        {
            return (plane == 0) ? width : (width + 1) / 2;
        }

        public int GetPlaneHeight(uint plane)
        {
            return (plane == 0) ? height : (height + 1) / 2;
        }

        /// <summary>
        /// The first byte of a YUV 4:2:0 plane (0: Y, 1: U, 2: V).
        /// </summary>
        public IntPtr GetPlaneData(uint plane) => planes[plane];

        /// <summary>
        /// The distance in bytes between rows of a plane.
        /// </summary>
        public int GetPlaneStride(uint plane) => strides != null ? strides[plane] : GetPlaneWidth(plane);

        public ReadOnlySpan<Rgba32> Data => ReadOnlySpan<Rgba32>.Empty;

        public int Level => 0;

        public RectangleI Bounds { get; set; }

        private readonly IntPtr[] planes;
        private readonly int[]? strides;
        private readonly Action release;
        private readonly int width;
        private readonly int height;

        /// <summary>
        /// Uploads YUV 4:2:0 planes. When <paramref name="strides"/> is omitted, planes are assumed to be tightly packed.
        /// </summary>
        /// <param name="width">The width of the luma plane.</param>
        /// <param name="height">The height of the luma plane.</param>
        /// <param name="planes">The Y, U and V planes, which must stay valid until <paramref name="release"/> is called.</param>
        /// <param name="release">Called once the planes are no longer needed.</param>
        /// <param name="strides">Optional row stride in bytes for each plane.</param>
        internal VideoTextureUpload(int width, int height, IntPtr[] planes, Action release, int[]? strides = null)
        {
            this.width = width;
            this.height = height;
            this.planes = planes;
            this.strides = strides;
            this.release = release;
        }

        #region IDisposable Support

        public void Dispose() => release();

        #endregion
    }
}
