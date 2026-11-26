// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Textures;
using Texture = Veldrid.Texture;

namespace osu.Framework.Graphics.Veldrid.Textures
{
    /// <summary>
    /// A video frame that stays on the GPU: instead of providing plane data, it copies itself into a video texture's planes.
    /// Its <see cref="ITextureUpload.Bounds"/> give the frame size.
    /// </summary>
    internal interface IVeldridSharedVideoUpload : ITextureUpload
    {
        /// <summary>
        /// Copies the frame into the R8 Y, U and V textures of a video texture, on the draw thread.
        /// </summary>
        void CopyTo(IVeldridRenderer renderer, Texture y, Texture u, Texture v);
    }
}
