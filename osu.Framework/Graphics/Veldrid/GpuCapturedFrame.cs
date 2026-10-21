// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// A borrowed device-local frame, valid until disposal. Do not dispose the texture itself.
    /// Finish all encoder access before disposing this lease on the draw thread.
    /// Native Vulkan images are not external-memory handles and cannot be imported directly into another device.
    /// </summary>
    public sealed class GpuCapturedFrame : IDisposable
    {
        public Texture Texture { get; }
        public long FrameIndex { get; }
        public long Timestamp { get; }
        public bool IsUvOriginTopLeft { get; }
        private Action? release;

        internal GpuCapturedFrame(Texture texture, long frameIndex, long timestamp, bool isUvOriginTopLeft, Action release)
        {
            Texture = texture;
            FrameIndex = frameIndex;
            Timestamp = timestamp;
            IsUvOriginTopLeft = isUvOriginTopLeft;
            this.release = release;
        }

        public void Dispose()
        {
            release?.Invoke();
            release = null;
        }
    }
}
