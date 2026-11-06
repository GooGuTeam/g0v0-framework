// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// The largest square textures that could actually be allocated and rendered to on a device, as opposed to the size its driver reports.
    /// </summary>
    /// <param name="MaxRgbaSize">The largest verified RGBA size.</param>
    /// <param name="MaxSingleChannelSize">The largest verified single-channel (R8) size.</param>
    /// <param name="CoversReportedSize">Whether every probed RGBA size up to the probe's limit passed, so larger reported sizes are trusted.</param>
    /// <param name="Details">A description for logs.</param>
    public sealed record TextureSizeProbeResult(int MaxRgbaSize, int MaxSingleChannelSize, bool CoversReportedSize, string Details);
}
