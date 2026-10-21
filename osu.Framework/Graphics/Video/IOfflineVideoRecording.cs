// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Veldrid;

namespace osu.Framework.Graphics.Video
{
    public interface IOfflineVideoRecording : IDisposable
    {
        long WrittenFrames { get; }
        long WrittenAudioFrames { get; }
        int AudioFramesPerVideoFrame { get; }
        void WriteFrame(Texture texture, float[] audio);
        void Complete();
    }
}
