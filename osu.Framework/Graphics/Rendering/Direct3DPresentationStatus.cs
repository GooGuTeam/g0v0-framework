// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// A snapshot of the actual DXGI swap chain state, rather than the requested window mode.
    /// A null member indicates that its state could not be queried.
    /// </summary>
    /// <remarks>
    /// A flip-model swap chain does not imply Independent Flip or exclusive fullscreen.
    /// </remarks>
    public sealed record Direct3DPresentationStatus(Direct3DPresentationModel? Model, bool? ExclusiveFullscreen)
    {
        public bool? UsesFlipModel => Model == null ? null : Model is Direct3DPresentationModel.FlipSequential or Direct3DPresentationModel.FlipDiscard;
    }
}
