// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Textures;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    public interface IFrameBuffer : IDisposable
    {
        /// <summary>
        /// The framebuffer's backing texture.
        /// </summary>
        Texture Texture { get; }

        /// <summary>
        /// The framebuffer's texture size.
        /// </summary>
        Vector2 Size { get; set; }

        /// <summary>
        /// Binds the framebuffer.
        /// <para>Does not clear the buffer or reset the viewport/ortho.</para>
        /// </summary>
        void Bind();

        /// <summary>
        /// Unbinds the framebuffer.
        /// </summary>
        void Unbind();

        /// <summary>
        /// Whether the framebuffer has a depth attachment.
        /// </summary>
        internal bool HasDepth => false;
    }
}
