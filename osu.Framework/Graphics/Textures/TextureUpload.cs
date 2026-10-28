// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.IO;
using System.Runtime.InteropServices;
using osu.Framework.Extensions.ImageExtensions;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Imaging;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>
    /// Low level class for queueing texture uploads to the GPU.
    /// Should be manually disposed if not queued for upload via <see cref="Texture.SetData(ITextureUpload)"/>.
    /// </summary>
    public class TextureUpload : ITextureUpload
    {
        /// <summary>
        /// The target mipmap level to upload into.
        /// </summary>
        public int Level { get; set; }

        /// <summary>
        /// The target bounds for this upload. If not specified, will assume to be (0, 0, width, height).
        /// </summary>
        public RectangleI Bounds { get; set; }

        public virtual ReadOnlySpan<Rgba32> Data => pixelMemory.Span;

        public virtual int Width => image?.Width ?? 0;

        public virtual int Height => image?.Height ?? 0;

        public virtual osu.Framework.Graphics.Rendering.TexturePixelFormat Format => format;

        public virtual ReadOnlySpan<byte> RawData => MemoryMarshal.AsBytes(Data);

        private readonly osu.Framework.Graphics.Rendering.TexturePixelFormat format;

        /// <summary>
        /// The backing texture. A handle is kept to avoid early GC.
        /// </summary>
        private readonly Image<Rgba32> image;

        private ReadOnlyPixelMemory<Rgba32> pixelMemory;

        /// <summary>
        /// Create an upload from a <see cref="TextureUpload"/>. This is the preferred method.
        /// </summary>
        /// <param name="image">The texture to upload.</param>
        public TextureUpload(Image<Rgba32> image)
        {
            format = osu.Framework.Graphics.Rendering.TexturePixelFormat.R8G8B8A8Float;
            this.image = image;
            pixelMemory = image.CreateReadOnlyPixelMemory();
        }

        /// <summary>
        /// Create an upload from an arbitrary image stream.
        /// Note that this bypasses per-platform image loading optimisations.
        /// Use <see cref="TextureLoaderStore"/> as provided from GameHost where possible.
        /// </summary>
        /// <param name="stream">The image content.</param>
        public TextureUpload(Stream stream)
            : this(LoadFromStream<Rgba32>(stream))
        {
        }

        internal static Image<TPixel> LoadFromStream<TPixel>(Stream stream) where TPixel : unmanaged, IPixel<TPixel>
            => Image.Load<TPixel>(stream);

        /// <summary>
        /// Create an empty upload. Used by <see cref="IFrameBuffer"/> for initialisation.
        /// </summary>
        internal TextureUpload()
            : this(osu.Framework.Graphics.Rendering.TexturePixelFormat.R8G8B8A8Float)
        {
        }

        /// <summary>
        /// Create an empty upload matching a framebuffer's storage format.
        /// </summary>
        internal TextureUpload(osu.Framework.Graphics.Rendering.TexturePixelFormat format)
        {
            this.format = format;
        }

        #region IDisposable Support

        private bool disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (disposed)
                return;

            image?.Dispose();
            pixelMemory.Dispose();

            disposed = true;
        }

        #endregion
    }
}
