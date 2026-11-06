// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
using Veldrid;
using PixelFormat = Veldrid.PixelFormat;
using Texture = Veldrid.Texture;

namespace osu.Framework.Graphics.Veldrid.Textures
{
    internal class VeldridTexture : IVeldridTexture
    {
        private readonly Queue<ITextureUpload> uploadQueue = new Queue<ITextureUpload>();

        // Published under the queue lock so empty checks never read Queue.Count without synchronisation.
        private int pendingUploadCount;

        IRenderer INativeTexture.Renderer => Renderer;
        TextureFilteringMode INativeTexture.FilteringMode => filteringMode;
        TexturePixelFormat INativeTexture.PixelFormat => pixelFormat;

        public string Identifier
        {
            get
            {
                if (!Available || resources == null)
                    return "-";

                return resources.Texture.Name;
            }
        }

        public int MaxSize => Renderer.MaxTextureSize;

        public virtual int Width { get; set; }
        public virtual int Height { get; set; }
        public virtual int GetByteSize() => Width * Height * (pixelFormat == TexturePixelFormat.R8Float ? 1 : 4);
        public bool Available { get; private set; } = true;

        internal ITextureUpload CreateEmptyUpload() => new TextureUpload(pixelFormat);

        ulong INativeTexture.TotalBindCount { get; set; }

        public bool BypassTextureUploadQueueing { get; set; }

        private readonly bool manualMipmaps;

        private readonly SamplerFilter nativeFilteringMode;
        private readonly TextureFilteringMode filteringMode;
        private readonly PixelFormat nativePixelFormat;
        private readonly TexturePixelFormat pixelFormat;
        private readonly Color4? initialisationColour;

        public ulong BindCount { get; protected set; }

        public RectangleI Bounds => new RectangleI(0, 0, Width, Height);

        protected virtual TextureUsage Usages
        {
            get
            {
                var usages = TextureUsage.Sampled | TextureUsage.RenderTarget;

                if (!manualMipmaps)
                    usages |= TextureUsage.GenerateMipmaps;

                return usages;
            }
        }

        protected readonly IVeldridRenderer Renderer;

        /// <summary>
        /// Creates a new <see cref="VeldridTexture"/>.
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        /// <param name="textureFormat">The pixel format of the texture.</param>
        /// <param name="manualMipmaps">Whether manual mipmaps will be uploaded to the texture. If false, the texture will compute mipmaps automatically.</param>
        /// <param name="filteringMode">The filtering mode.</param>
        /// <param name="initialisationColour">The colour to initialise texture levels with (in the case of sub region initial uploads). If null, no initialisation is provided out-of-the-box.</param>
        public VeldridTexture(IVeldridRenderer renderer, int width, int height, TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, bool manualMipmaps = false, TextureFilteringMode filteringMode = TextureFilteringMode.Linear,
                              Color4? initialisationColour = null)
        {
            this.manualMipmaps = manualMipmaps;
            this.filteringMode = filteringMode;
            nativeFilteringMode = filteringMode.ToSamplerFilter();
            this.initialisationColour = initialisationColour;
            pixelFormat = textureFormat;
            nativePixelFormat = textureFormat.ToPixelFormat();

            Renderer = renderer;
            Width = width;
            Height = height;
        }

        #region Disposal

        ~VeldridTexture()
        {
            Dispose(false);
        }

        private bool isDisposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            Renderer.ScheduleDisposal(static texture =>
            {
                while (texture.tryGetNextUpload(out var upload))
                    upload.Dispose();

                texture.memoryLease?.Dispose();

                texture.resources?.Dispose();
                texture.resources = null;

                texture.bindlessView?.Dispose();
                texture.bindlessView = null;

                texture.Available = false;
            }, this);
        }

        #endregion

        #region Memory Tracking

        private readonly List<long> levelMemoryUsage = new List<long>();
        private NativeMemoryTracker.NativeMemoryLease? memoryLease;

        private void updateMemoryUsage(int level, long newUsage)
        {
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

        private readonly VeldridTextureResources?[] resourcesArray = new VeldridTextureResources?[1];

        private VeldridTextureResources? resources
        {
            get => resourcesArray[0];
            set => resourcesArray[0] = value;
        }

        private TextureView? bindlessView;

        /// <summary>
        /// A view of this texture limited to the mip levels its own sampler would use, for bindless texture tables, which share samplers.
        /// </summary>
        internal TextureView? GetBindlessView()
        {
            var texture = resources?.Texture;

            if (texture == null)
                return null;

            bool useUploadMipmaps = manualMipmaps || maximumUploadedLod > 0;
            uint baseLevel = (uint)(MipLevel ?? 0);
            uint levels = MipLevel != null ? 1 : Math.Min(texture.MipLevels, (uint)(useUploadMipmaps ? maximumUploadedLod : IRenderer.MAX_MIPMAP_LEVELS) + 1);

            if (bindlessView != null && bindlessView.Target == texture && bindlessView.BaseMipLevel == baseLevel && bindlessView.MipLevels == levels)
                return bindlessView;

            if (bindlessView != null)
                Renderer.ScheduleDisposal(static view => view.Dispose(), bindlessView);

            return bindlessView = Renderer.Factory.CreateTextureView(new TextureViewDescription(texture, baseLevel, levels, 0, 1));
        }

        internal bool UsesNearestFiltering => nativeFilteringMode == SamplerFilter.MinPointMagPointMipPoint;

        public virtual IReadOnlyList<VeldridTextureResources> GetResourceList()
        {
            if (resources == null)
                return Array.Empty<VeldridTextureResources>();

            return resourcesArray!;
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

        public bool Upload()
        {
            if (!Available)
                return false;

            // We should never run raw Veldrid calls on another thread than the draw thread due to race conditions.
            ThreadSafety.EnsureDrawThread();

            bool uploaded = false;

            while (tryGetNextUpload(out ITextureUpload? upload))
            {
                using (upload)
                {
                    ITextureUpload effectiveUpload = upload;
                    IDisposable? convertedDisposable = null;

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

                        Texture? previousTexture = resources?.Texture;

                        DoUpload(effectiveUpload);

                        uploaded = true;

                        // Only level-0 data uploaded into an existing texture can be followed by regional regeneration.
                        if (resources?.Texture != previousTexture || effectiveUpload.Level != 0)
                            requiresFullMipmaps = true;
                        else if (!effectiveUpload.RawData.IsEmpty)
                            uploadedRegions.Add(effectiveUpload.Bounds);
                    }
                    finally
                    {
                        convertedDisposable?.Dispose();
                    }
                }
            }

            if (uploaded && !manualMipmaps)
            {
                Debug.Assert(resources != null);

                if (canGenerateRegionMipmaps && MipmapRegions.TryPlan(uploadedRegions, Width, Height, mipmapRegions))
                {
                    foreach (var region in mipmapRegions)
                        Renderer.GenerateMipmaps(this, region);

                    RegionMipmapGenerations += mipmapRegions.Count;
                }
                else
                {
                    Renderer.GenerateMipmaps(this);
                    requiresFullMipmaps = false;
                }
            }

            uploadedRegions.Clear();

            return uploaded;
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

        private bool tryGetNextUpload([NotNullWhen(true)] out ITextureUpload? upload)
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

        private int? mipLevel;

        public int? MipLevel
        {
            get => mipLevel;
            set
            {
                if (mipLevel == value)
                    return;

                mipLevel = value;

                resources?.Sampler = createSampler();
            }
        }

        /// <summary>
        /// The maximum number of mip levels provided by an <see cref="ITextureUpload"/>.
        /// </summary>
        /// <remarks>
        /// This excludes automatic generation of mipmaps via the graphics backend.
        /// </remarks>
        private int maximumUploadedLod;

        private readonly List<RectangleI> uploadedRegions = new List<RectangleI>();
        private readonly List<RectangleI> mipmapRegions = new List<RectangleI>();

        /// <summary>
        /// Whether levels 1 and above have not yet been fully generated from the current level 0. Regional regeneration
        /// relies on a whole-texture generation having initialised every level first.
        /// </summary>
        private bool requiresFullMipmaps = true;

        /// <summary>
        /// The number of regional mipmap generations performed, for diagnostics and tests.
        /// </summary>
        internal int RegionMipmapGenerations { get; private set; }

        private bool canGenerateRegionMipmaps
            => !requiresFullMipmaps
               && MipmapRegions.SupportsFormat(pixelFormat)
               && resources is { Texture.MipLevels: > IRenderer.MAX_MIPMAP_LEVELS }
               && GetResourceList().Count == 1;

        /// <summary>
        /// Regenerates mipmap levels 1 to <see cref="IRenderer.MAX_MIPMAP_LEVELS"/> for an aligned region of a texture.
        /// The region is copied into a small scratch texture, which the backend generates mipmaps for, and the resulting
        /// levels are copied back. Only copies and the backend's own mipmap generation are used.
        /// </summary>
        internal static void GenerateRegionMipmaps(IVeldridRenderer renderer, CommandList commands, VeldridTexture texture, RectangleI region)
        {
            MipmapRegions.ThrowIfMisaligned(region);

            Texture target = texture.GetResourceList()[0].Texture;

            var description = TextureDescription.Texture2D((uint)region.Width, (uint)region.Height, IRenderer.MAX_MIPMAP_LEVELS + 1, 1, target.Format,
                TextureUsage.Sampled | TextureUsage.RenderTarget | TextureUsage.GenerateMipmaps);
            Texture scratch = renderer.Factory.CreateTexture(ref description);

            commands.CopyTexture(target, (uint)region.X, (uint)region.Y, 0, 0, 0, scratch, 0, 0, 0, 0, 0, (uint)region.Width, (uint)region.Height, 1, 1);
            commands.GenerateMipmaps(scratch);

            for (int level = 1; level <= IRenderer.MAX_MIPMAP_LEVELS; level++)
            {
                commands.CopyTexture(scratch, 0, 0, 0, (uint)level, 0, target, (uint)(region.X >> level), (uint)(region.Y >> level), 0, (uint)level, 0,
                    (uint)(region.Width >> level), (uint)(region.Height >> level), 1, 1);
            }

            renderer.ScheduleDisposal(static t => t.Dispose(), scratch);
        }

        private Sampler createSampler()
        {
            bool useUploadMipmaps = manualMipmaps || maximumUploadedLod > 0;
            int maximumLod = useUploadMipmaps ? maximumUploadedLod : IRenderer.MAX_MIPMAP_LEVELS;

            var samplerDescription = new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = nativeFilteringMode,
                MinimumLod = (uint)(MipLevel ?? 0),
                MaximumLod = (uint)(MipLevel ?? maximumLod),
                MaximumAnisotropy = 0,
            };

            return Renderer.Factory.CreateSampler(ref samplerDescription);
        }

        protected virtual void DoUpload(ITextureUpload upload)
        {
            Texture? texture = resources?.Texture;
            Sampler? sampler = resources?.Sampler;

            if (texture == null || texture.Width != Width || texture.Height != Height)
            {
                if (texture != null)
                    Renderer.ScheduleDisposal(static t => t.Dispose(), texture);

                var textureDescription = TextureDescription.Texture2D((uint)Width, (uint)Height, (uint)CalculateMipmapLevels(Width, Height), 1, nativePixelFormat, Usages);
                texture = Renderer.Factory.CreateTexture(ref textureDescription);

                // todo: we may want to look into not having to allocate chunks of zero byte region for initialising textures
                // similar to how OpenGL allows calling glTexImage2D with null data pointer.
                initialiseLevel(texture, 0, Width, Height);

                maximumUploadedLod = 0;
            }

            int lastMaximumUploadedLod = maximumUploadedLod;

            if (!upload.RawData.IsEmpty)
            {
                // ensure all mip levels up to the target level are initialised.
                // generally we always upload at level 0, so this won't run.
                if (upload.Level > maximumUploadedLod)
                {
                    for (int i = maximumUploadedLod + 1; i <= upload.Level; i++)
                        initialiseLevel(texture, i, Width >> i, Height >> i);

                    maximumUploadedLod = upload.Level;
                }

                unsafe
                {
                    fixed (byte* data = upload.RawData)
                        Renderer.UpdateTexture(texture, upload.Bounds.X >> upload.Level, upload.Bounds.Y >> upload.Level,
                            upload.Bounds.Width >> upload.Level, upload.Bounds.Height >> upload.Level, upload.Level, (IntPtr)data,
                            upload.Bounds.Width * (pixelFormat == TexturePixelFormat.R8Float ? 1 : 4));
                }
            }

            if (sampler == null || maximumUploadedLod > lastMaximumUploadedLod)
            {
                if (sampler != null)
                    Renderer.ScheduleDisposal(static s => s.Dispose(), sampler);

                sampler = createSampler();
            }

            resources = new VeldridTextureResources(Renderer, texture, sampler);
        }

        private unsafe void initialiseLevel(Texture texture, int level, int width, int height)
        {
            if (initialisationColour == null)
                return;

            if (pixelFormat == TexturePixelFormat.R8Float)
            {
                byte[] coverage = new byte[checked(width * height)];
                Array.Fill(coverage, (byte)Math.Clamp((int)MathF.Round(initialisationColour.Value.R * 255), 0, 255));
                updateMemoryUsage(level, coverage.Length);
                fixed (byte* data = coverage)
                    Renderer.UpdateTexture(texture, 0, 0, width, height, level, (IntPtr)data, width);
                return;
            }

            if (pixelFormat == TexturePixelFormat.R8Float)
            {
                byte[] coverage = new byte[checked(width * height)];
                Array.Fill(coverage, (byte)Math.Clamp((int)MathF.Round(initialisationColour.Value.R * 255), 0, 255));
                updateMemoryUsage(level, coverage.Length);
                fixed (byte* data = coverage)
                    Renderer.UpdateTexture(texture, 0, 0, width, height, level, (IntPtr)data, width);
                return;
            }

            var rgbaColour = new Rgba32(new Vector4(initialisationColour.Value.R, initialisationColour.Value.G, initialisationColour.Value.B, initialisationColour.Value.A));

            // it is faster to initialise without a background specification if transparent black is all that's required.
            using var image = initialisationColour == null
                ? new Image<Rgba32>(width, height)
                : new Image<Rgba32>(width, height, rgbaColour);

            using (var pixels = image.CreateReadOnlyPixelSpan())
            {
                updateMemoryUsage(level, (long)width * height * sizeof(Rgba32));
                Renderer.UpdateTexture(texture, 0, 0, width, height, level, pixels.Span);
            }
        }

        // todo: should this be limited to MAX_MIPMAP_LEVELS or was that constant supposed to be for automatic mipmap generation only?
        // previous implementation was allocating mip levels all the way to 1x1 size when an ITextureUpload.Level > 0, therefore it's not limited there.
        protected static int CalculateMipmapLevels(int width, int height) => 1 + (int)Math.Floor(Math.Log(Math.Max(width, height), 2));
    }
}
