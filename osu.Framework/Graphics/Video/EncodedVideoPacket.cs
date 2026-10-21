// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// A copied H.264 or raw AAC access unit. Times use 100 ns units; configuration packets
    /// contain the H.264 sequence header supplied by Media Foundation.
    /// </summary>
    public sealed class EncodedVideoPacket
    {
        private readonly byte[] data;
        public ReadOnlyMemory<byte> Data => data;
        public bool IsVideo { get; }
        public bool IsKeyFrame { get; }
        public bool IsConfiguration { get; }
        public long PresentationTime { get; }
        public long DecodeTime { get; }
        public long Duration { get; }

        internal EncodedVideoPacket(byte[] data, bool video, bool keyFrame, bool configuration, long pts, long dts, long duration)
        {
            this.data = data;
            IsVideo = video;
            IsKeyFrame = keyFrame;
            IsConfiguration = configuration;
            PresentationTime = pts;
            DecodeTime = dts;
            Duration = duration;
        }
    }
}
