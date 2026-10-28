// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Textures;
using FFmpeg.AutoGen;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Video
{
    public sealed unsafe class VideoTextureUpload : ITextureUpload
    {
        /// <summary>
        /// The FFmpeg frame holding the planes, or null if they come from another decoder.
        /// </summary>
        public AVFrame* Frame => ffmpegFrame == null ? null : ffmpegFrame.Pointer;

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
        public IntPtr GetPlaneData(uint plane) => ffmpegFrame == null ? planes![plane] : new IntPtr(Frame->data[plane]);

        /// <summary>
        /// The distance in bytes between rows of a plane.
        /// </summary>
        public int GetPlaneStride(uint plane) => ffmpegFrame == null ? GetPlaneWidth(plane) : Frame->linesize[plane];

        public ReadOnlySpan<Rgba32> Data => ReadOnlySpan<Rgba32>.Empty;

        public int Level => 0;

        public RectangleI Bounds { get; set; }

        private readonly FFmpegFrame? ffmpegFrame;
        private readonly IntPtr[]? planes;
        private readonly Action? release;
        private readonly int width;
        private readonly int height;

        /// <summary>
        /// Sets the frame containing the data to be uploaded.
        /// </summary>
        /// <param name="ffmpegFrame">The frame to upload.</param>
        internal VideoTextureUpload(FFmpegFrame ffmpegFrame)
        {
            this.ffmpegFrame = ffmpegFrame;
            width = ffmpegFrame.Pointer->width;
            height = ffmpegFrame.Pointer->height;
        }

        /// <summary>
        /// Uploads tightly packed YUV 4:2:0 planes, each row as wide as its plane.
        /// </summary>
        /// <param name="width">The width of the luma plane.</param>
        /// <param name="height">The height of the luma plane.</param>
        /// <param name="planes">The Y, U and V planes, which must stay valid until <paramref name="release"/> is called.</param>
        /// <param name="release">Called once the planes are no longer needed.</param>
        internal VideoTextureUpload(int width, int height, IntPtr[] planes, Action release)
        {
            this.width = width;
            this.height = height;
            this.planes = planes;
            this.release = release;
        }

        #region IDisposable Support

        public void Dispose()
        {
            if (ffmpegFrame != null)
                ffmpegFrame.Return();
            else
                release!();
        }

        #endregion
    }
}
