// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>A compatibility observation, never permission to enable a renderer path.</summary>
    public sealed record BindlessTextureProbeResult(BindlessTextureProbeStatus Status, string Details)
    {
        /// <summary>The current framework still uses bound textures on every backend.</summary>
        public bool RendererImplemented => false;
    }
}
