// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// The presentation model used by a Direct3D swap chain, as reported by DXGI.
    /// </summary>
    public enum Direct3DPresentationModel
    {
        BitBltDiscard,
        BitBltSequential,
        FlipSequential,
        FlipDiscard,
    }
}
