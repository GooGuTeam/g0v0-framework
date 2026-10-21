// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Immutable settings captured before live recording or offline export starts.
    /// </summary>
    public sealed class VideoRecordingOptions
    {
        public int FrameRate { get; }
        public int Bitrate { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>
        /// H.264 profile IDC: 0 for automatic, 66 for Baseline, 77 for Main, or 100 for High.
        /// </summary>
        public int H264Profile { get; }

        public VideoRecordingOptions(int frameRate = 60, int bitrate = 12000000, int h264Profile = 0, int width = 1920, int height = 1080)
        {
            if (frameRate is not (30 or 60 or 120))
                throw new ArgumentOutOfRangeException(nameof(frameRate));
            if (bitrate < 1000000 || bitrate > 100000000)
                throw new ArgumentOutOfRangeException(nameof(bitrate));
            if (h264Profile is not (0 or 66 or 77 or 100))
                throw new ArgumentOutOfRangeException(nameof(h264Profile));
            if (width < 2 || height < 2 || width > 3840 || height > 2160 || (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            Width = width;
            Height = height;
            FrameRate = frameRate;
            Bitrate = bitrate;
            H264Profile = h264Profile;
        }
    }
}
