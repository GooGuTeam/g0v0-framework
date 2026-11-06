// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using osu.Framework.Development;
using osu.Framework.Extensions.ImageExtensions;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osuTK.Graphics;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.OpenGL.Textures
{
    internal class GLTexture : INativeTexture
    {
        protected readonly GLRenderer Renderer;
        private readonly TextureFilteringMode filteringMode;
        private readonly TexturePixelFormat pixelFormat;

        /// <summary>
        /// Creates an empty (allocation-only) upload matching this texture's format, e.g. for single-channel framebuffers.
        /// </summary>
        internal ITextureUpload CreateEmptyUpload() => new TextureUpload(pixelFormat);
        private readonly Queue<ITextureUpload> uploadQueue = new Queue<ITextureUpload>();

        // Published under the queue lock so empty checks never read Queue.Count without synchronisation.
        private int pendingUploadCount;

        IRenderer INativeTexture.Renderer => Renderer;

        public string Identifier
        {
            get
            {
                if (!Available || textureId == 0)
                    return "-";

                return textureId.ToString();
            }
        }

        public int MaxSize => Renderer.MaxTextureSize;

        TextureFilteringMode INativeTexture.FilteringMode => filteringMode;
        TexturePixelFormat INativeTexture.PixelFormat => pixelFormat;

        public virtual int Width { get; set; }
        public virtual int Height { get; set; }
        public virtual int GetByteSize() => Width * Height * (pixelFormat == TexturePixelFormat.R8Float ? 1 : 4);
        public bool Available { get; private set; } = true;

        private int? mipLevel;

        public int? MipLevel
        {
            get => mipLevel;
            set
            {
                mipLevel = value;
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinLod, mipLevel ?? 0);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLod, mipLevel ?? IRenderer.MAX_MIPMAP_LEVELS);
            }
        }

        ulong INativeTexture.TotalBindCount { get; set; }

        public bool BypassTextureUploadQueueing { get; set; }

        protected TextureComponentCount InternalFormat { get; private set; }
        protected All InternalFilteringMode { get; private set; }
        protected PixelFormat PixelFormat { get; private set; }
        protected PixelType PixelType { get; private set; }

        private int internalWidth;
        private int internalHeight;
        private bool manualMipmaps;

        private readonly List<RectangleI> uploadedRegions = new List<RectangleI>();
        private readonly List<RectangleI> mipmapRegions = new List<RectangleI>();

        /// <summary>
        /// Whether levels 1 and above have not yet been fully generated from the current level 0. Regional regeneration
        /// relies on a whole-texture generation having allocated and initialised every level first.
        /// </summary>
        private bool requiresFullMipmaps = true;

        /// <summary>
        /// The number of regional mipmap generations performed, for diagnostics and tests.
        /// </summary>
        internal int RegionMipmapGenerations { get; private set; }

        private bool tryGenerateRegionMipmaps()
        {
            if (requiresFullMipmaps || !Renderer.SupportsRegionMipmaps || !MipmapRegions.SupportsFormat(pixelFormat))
                return false;

            if (!MipmapRegions.TryPlan(uploadedRegions, Width, Height, mipmapRegions))
                return false;

            foreach (var region in mipmapRegions)
            {
                if (!Renderer.TryGenerateMipmaps(textureId, region, InternalFormat, PixelFormat, PixelType))
                {
                    requiresFullMipmaps = true;
                    return false;
                }

                RegionMipmapGenerations++;
            }

            return true;
        }

        private readonly Color4? initialisationColour;

        /// <summary>
        /// Creates a new <see cref="GLTexture"/>.
        /// </summary>
        /// <param name="renderer"></param>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        /// <param name="textureFormat">The pixel format of the texture.</param>
        /// <param name="manualMipmaps">Whether manual mipmaps will be uploaded to the texture. If false, the texture will compute mipmaps automatically.</param>
        /// <param name="filteringMode">The filtering mode.</param>
        /// <param name="initialisationColour">The colour to initialise texture levels with (in the case of sub region initial uploads). If null, no initialisation is provided out-of-the-box.</param>
        public GLTexture(GLRenderer renderer, int width, int height, TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, bool manualMipmaps = false, TextureFilteringMode filteringMode = TextureFilteringMode.Linear, Color4? initialisationColour = null)
        {
            Renderer = renderer;
            Width = width;
            Height = height;
            this.filteringMode = filteringMode;
            pixelFormat = textureFormat;

            setFormat(textureFormat);
            setFilteringMode(filteringMode);

            this.manualMipmaps = manualMipmaps;
            this.initialisationColour = initialisationColour;
        }

        private void setFilteringMode(TextureFilteringMode filteringMode)
        {
            switch (filteringMode)
            {
                case TextureFilteringMode.Linear:
                    InternalFilteringMode = All.Linear;
                    break;

                case TextureFilteringMode.Nearest:
                    InternalFilteringMode = All.Nearest;
                    break;

                default:
                    throw new ArgumentException($"Unsupported filtering mode: {filteringMode}", nameof(filteringMode));
            }
        }

        private void setFormat(TexturePixelFormat format)
        {
            // reference: https://registry.khronos.org/OpenGL-Refpages/gl4/html/glTexImage2D.xhtml
            // add more formats as desired
            switch (format)
            {
                case TexturePixelFormat.R8G8B8A8Float:
                    InternalFormat = TextureComponentCount.Rgba8;
                    PixelFormat = PixelFormat.Rgba;
                    PixelType = PixelType.UnsignedByte;
                    break;

                case TexturePixelFormat.R16Float:
                    InternalFormat = TextureComponentCount.R16f;
                    PixelFormat = PixelFormat.Red;
                    PixelType = PixelType.HalfFloat;
                    break;

                case TexturePixelFormat.R8Float:
                    InternalFormat = TextureComponentCount.R8;
                    PixelFormat = PixelFormat.Red;
                    PixelType = PixelType.UnsignedByte;
                    break;

                default:
                    throw new ArgumentException($"Unsupported render buffer format: {format}", nameof(format));
            }
        }

        #region Disposal

        private bool isDisposed;

        public void Dispose()
        {
            if (isDisposed)
                return;

            isDisposed = true;

            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~GLTexture()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            Renderer.ScheduleDisposal(static texture =>
            {
                while (texture.tryGetNextUpload(out var upload))
                    upload.Dispose();

                int disposableId = texture.textureId;

                if (disposableId <= 0)
                    return;

                GL.DeleteTextures(1, new[] { disposableId });

                texture.memoryLease?.Dispose();

                texture.textureId = 0;
                texture.Available = false;
            }, this);
        }

        #endregion

        #region Memory Tracking

        private List<long> levelMemoryUsage = new List<long>();

        private NativeMemoryTracker.NativeMemoryLease memoryLease;

        private void updateMemoryUsage(int level, long newUsage)
        {
            levelMemoryUsage ??= new List<long>();

            while (level >= levelMemoryUsage.Count)
                levelMemoryUsage.Add(0);

            levelMemoryUsage[level] = newUsage;

            memoryLease?.Dispose();
            memoryLease = NativeMemoryTracker.AddMemory(this, getMemoryUsage());
        }

        private long getMemoryUsage()
        {
            long usage = 0;

            for (int i = 0; i < levelMemoryUsage.Count; i++)
                usage += levelMemoryUsage[i];

            return usage;
        }

        #endregion

        private int textureId;

        public virtual int TextureId
        {
            get
            {
                ObjectDisposedException.ThrowIf(!Available, this);

                return textureId;
            }
        }

        public void FlushUploads()
        {
            while (tryGetNextUpload(out var upload))
                upload.Dispose();
        }

        public void SetData(ITextureUpload upload)
        {
            lock (uploadQueue)
            {
                if (uploadQueue.Count >= 100 && uploadQueue.Count % 100 == 0)
                    Logger.Log($"Texture {Identifier}'s upload queue is large ({uploadQueue.Count})");

                bool requireUpload = uploadQueue.Count == 0;
                uploadQueue.Enqueue(upload);
                Volatile.Write(ref pendingUploadCount, uploadQueue.Count);

                if (requireUpload && !BypassTextureUploadQueueing)
                    Renderer.EnqueueTextureUpload(this);
            }
        }

        public unsafe bool Upload()
        {
            if (!Available)
                return false;

            uploadedRegions.Clear();

            // We should never run raw OGL calls on another thread than the main thread due to race conditions.
            ThreadSafety.EnsureDrawThread();

            while (tryGetNextUpload(out ITextureUpload upload))
            {
                using (upload)
                {
                    ITextureUpload effectiveUpload = upload;
                    IDisposable convertedDisposable = null;

                    if (upload is AlphaTextureUpload alpha && pixelFormat == TexturePixelFormat.R8G8B8A8Float)
                    {
                        var converted = alpha.ToRgbaUpload();
                        effectiveUpload = converted;
                        convertedDisposable = converted;
                    }

                    try
                    {
                        if (effectiveUpload.Format != pixelFormat)
                            throw new InvalidOperationException($"Texture upload format {effectiveUpload.Format} does not match texture format {pixelFormat}.");

                        int previousAlignment = 4;
                        if (pixelFormat == TexturePixelFormat.R8Float)
                        {
                            GL.GetInteger(GetPName.UnpackAlignment, out previousAlignment);
                            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                        }

                        try
                        {
                            (int id, int width, int height) previous = (textureId, internalWidth, internalHeight);

                            fixed (byte* ptr = effectiveUpload.RawData)
                                DoUpload(effectiveUpload, (IntPtr)ptr);

                            // Only level-0 data uploaded into an existing allocation can be followed by regional regeneration.
                            if (previous != (textureId, internalWidth, internalHeight) || effectiveUpload.Level != 0 || effectiveUpload.RawData.IsEmpty)
                                requiresFullMipmaps = true;
                        }
                        finally
                        {
                            if (pixelFormat == TexturePixelFormat.R8Float)
                                GL.PixelStore(PixelStoreParameter.UnpackAlignment, previousAlignment);
                        }

                        uploadedRegions.Add(effectiveUpload.Bounds);
                    }
                    finally
                    {
                        convertedDisposable?.Dispose();
                    }
                }
            }

            if (uploadedRegions.Count != 0 && !manualMipmaps)
            {
                if (!tryGenerateRegionMipmaps())
                {
                    GL.Hint(HintTarget.GenerateMipmapHint, HintMode.Nicest);
                    GL.GenerateMipmap(TextureTarget.Texture2D);
                    requiresFullMipmaps = false;
                }
            }

            return uploadedRegions.Count != 0;
        }

        public bool UploadComplete
        {
            get
            {
                lock (uploadQueue)
                    return uploadQueue.Count == 0;
            }
        }

        /// <summary>
        /// Whether the texture is currently queued for upload.
        /// </summary>
        public bool IsQueuedForUpload { get; set; }

        private bool tryGetNextUpload(out ITextureUpload upload)
        {
            // Recheck on every call: atlas uploads may arrive between two glyph draws in the same frame.
            if (Volatile.Read(ref pendingUploadCount) == 0)
            {
                upload = null;
                return false;
            }

            lock (uploadQueue)
            {
                if (uploadQueue.Count == 0)
                {
                    upload = null;
                    return false;
                }

                upload = uploadQueue.Dequeue();
                Volatile.Write(ref pendingUploadCount, uploadQueue.Count);
                return true;
            }
        }

        protected virtual void DoUpload(ITextureUpload upload, IntPtr dataPointer)
        {
            // Do we need to generate a new texture?
            if (textureId <= 0 || internalWidth != Width || internalHeight != Height)
            {
                internalWidth = Width;
                internalHeight = Height;

                // We only need to generate a new texture if we don't have one already. Otherwise just re-use the current one.
                if (textureId <= 0)
                {
                    int[] textures = new int[1];
                    GL.GenTextures(1, textures);

                    textureId = textures[0];

                    Renderer.BindTexture(this);

                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, IRenderer.MAX_MIPMAP_LEVELS);

                    // These shouldn't be required, but on some older Intel drivers the MAX_LOD chosen by the shader isn't clamped to the MAX_LEVEL from above, resulting in disappearing textures.
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinLod, 0);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLod, IRenderer.MAX_MIPMAP_LEVELS);

                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                        (int)(manualMipmaps ? InternalFilteringMode : InternalFilteringMode == All.Linear ? All.LinearMipmapLinear : All.Nearest));
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)filteringMode);

                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                }
                else
                    Renderer.BindTexture(this);

                if ((Width == upload.Bounds.Width && Height == upload.Bounds.Height) || dataPointer == IntPtr.Zero)
                {
                    updateMemoryUsage(upload.Level, (long)Width * Height * (pixelFormat == TexturePixelFormat.R8Float ? 1 : 4));
                    GL.TexImage2D(TextureTarget2d.Texture2D, upload.Level, InternalFormat, Width, Height, 0, PixelFormat, PixelType, dataPointer);
                }
                else
                {
                    initializeLevel(upload.Level, Width, Height);

                    GL.TexSubImage2D(TextureTarget2d.Texture2D, upload.Level, upload.Bounds.X, upload.Bounds.Y, upload.Bounds.Width, upload.Bounds.Height, PixelFormat,
                        PixelType, dataPointer);
                }
            }
            // Just update content of the current texture
            else if (dataPointer != IntPtr.Zero)
            {
                Renderer.BindTexture(this);

                if (!manualMipmaps && upload.Level > 0)
                {
                    //allocate mipmap levels
                    int level = 1;
                    int d = 2;

                    while (Width / d > 0)
                    {
                        initializeLevel(level, Width / d, Height / d);
                        level++;
                        d *= 2;
                    }

                    manualMipmaps = true;
                }

                int div = (int)Math.Pow(2, upload.Level);

                GL.TexSubImage2D(TextureTarget2d.Texture2D, upload.Level, upload.Bounds.X / div, upload.Bounds.Y / div, upload.Bounds.Width / div, upload.Bounds.Height / div,
                    PixelFormat, PixelType, dataPointer);
            }
        }

        private unsafe void initializeLevel(int level, int width, int height)
        {
            if (initialisationColour == null)
                return;

            if (pixelFormat == TexturePixelFormat.R8Float)
            {
                byte[] coverage = new byte[checked(width * height)];
                Array.Fill(coverage, (byte)Math.Clamp((int)MathF.Round(initialisationColour.Value.R * 255), 0, 255));
                updateMemoryUsage(level, coverage.Length);
                fixed (byte* ptr = coverage)
                    GL.TexImage2D(TextureTarget2d.Texture2D, level, InternalFormat, width, height, 0, PixelFormat, PixelType, (IntPtr)ptr);
                return;
            }

            var rgbaColour = new Rgba32(new Vector4(initialisationColour.Value.R, initialisationColour.Value.G, initialisationColour.Value.B, initialisationColour.Value.A));

            // it is faster to initialise without a background specification if transparent black is all that's required.
            using var image = initialisationColour == null
                ? new Image<Rgba32>(width, height)
                : new Image<Rgba32>(width, height, rgbaColour);

            using (var pixels = image.CreateReadOnlyPixelSpan())
            {
                updateMemoryUsage(level, (long)width * height * 4);
                GL.TexImage2D(TextureTarget2d.Texture2D, level, InternalFormat, width, height, 0, PixelFormat, PixelType,
                    ref MemoryMarshal.GetReference(pixels.Span));
            }
        }
    }
}
