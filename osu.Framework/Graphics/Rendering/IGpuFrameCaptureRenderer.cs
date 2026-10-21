// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Veldrid;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Provides bounded GPU-only frame capture. All operations must run on the draw thread.
    /// </summary>
    public interface IGpuFrameCaptureRenderer
    {
        /// <summary>
        /// Starts capturing before presentation. Dispose the session before renderer shutdown.
        /// Only one session may be active. No encoding or CPU readback is performed.
        /// </summary>
        GpuFrameCaptureSession StartGpuFrameCapture(int capacity = 3);
    }
}
