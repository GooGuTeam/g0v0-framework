// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Exposes presentation diagnostics for renderers which can use a Direct3D backend.
    /// </summary>
    public interface IDirect3DRenderer
    {
        /// <summary>
        /// The latest DXGI state, refreshed on the draw thread.
        /// Null if the active backend is not Direct3D.
        /// </summary>
        Direct3DPresentationStatus? PresentationStatus { get; }
    }
}
