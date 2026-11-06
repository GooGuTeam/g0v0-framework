// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Textures
{
    public interface ITextureUpload : IDisposable
    {
        /// <summary>
        /// The raw data to be uploaded.
        /// </summary>
        ReadOnlySpan<Rgba32> Data { get; }

        /// <summary>The storage format used by the upload.</summary>
        Rendering.TexturePixelFormat Format => Rendering.TexturePixelFormat.R8G8B8A8Float;

        /// <summary>Contiguous upload bytes in <see cref="Format"/> layout.</summary>
        ReadOnlySpan<byte> RawData => MemoryMarshal.AsBytes(Data);

        int Width => Bounds.Width;

        int Height => Bounds.Height;

        /// <summary>
        /// The target mipmap level to upload into.
        /// </summary>
        int Level { get; }

        /// <summary>
        /// The target bounds for this upload. If not specified, will assume to be (0, 0, width, height).
        /// </summary>
        RectangleI Bounds { get; set; }
    }
}
