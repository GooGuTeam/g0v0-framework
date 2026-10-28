// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using osu.Framework.Development;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Shaders.Types;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Lists;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using osu.Framework.Threading;
using osu.Framework.Timing;
using osuTK;
using osuTK.Graphics;
using osu.Framework.Imaging;
using RectangleF = osu.Framework.Graphics.Primitives.RectangleF;
using Texture = osu.Framework.Graphics.Textures.Texture;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Represents a base <see cref="IRenderer"/> implementation for working renderers.
    /// </summary>
    public abstract class Renderer : IRenderer
    {
        internal AtlasPackingStrategy TextureAtlasPacking { get; set; } = AtlasPackingStrategy.LegacyRows;

        private readonly Dictionary<(int size, TextureFilteringMode filteringMode, bool manualMipmaps), TextureAtlas> sharedTextureAtlases =
            new Dictionary<(int, TextureFilteringMode, bool), TextureAtlas>();

        /// <summary>
        /// The atlas shared by all <see cref="TextureStore"/>s with the given settings.
        /// </summary>
        /// <remarks>
        /// Separate atlases per store (the game, each skin, each ruleset) made sprites from different stores alternate between
        /// textures, splitting batches. Atlases never free regions and are not disposed by their stores, so sharing changes neither
        /// lifetime nor capacity: a full page is replaced by a new one and stays alive while its textures are referenced.
        /// </remarks>
        internal TextureAtlas GetSharedTextureAtlas(int size, TextureFilteringMode filteringMode, bool manualMipmaps)
        {
            lock (sharedTextureAtlases)
            {
                var key = (size, filteringMode, manualMipmaps);

                if (!sharedTextureAtlases.TryGetValue(key, out var atlas))
                    sharedTextureAtlases[key] = atlas = new TextureAtlas(this, size, size, manualMipmaps, filteringMode);

                return atlas;
            }
        }
        internal AtlasPackingStrategy FontAtlasPacking { get; set; } = AtlasPackingStrategy.FreeRectangles;
        internal bool LowResolutionFonts { get; set; }
        internal bool SingleChannelFontAtlas { get; set; }
        internal bool ShowOverdrawIndicator { get; set; }

        private readonly FrameDrawCapture? drawCapture = FrameDrawCapture.Create();
        internal GraphicsProbeCache? GraphicsProbeCache { get; set; }

        /// <summary>
        /// Whether to run the bindless texture probe. Other probes only need <see cref="GraphicsProbeCache"/>.
        /// </summary>
        internal bool ProbeBindlessTextures { get; set; } = true;

        /// <summary>
        /// The verified texture sizes on this device, or null if the probe did not run.
        /// </summary>
        public TextureSizeProbeResult? TextureSizeSupport { get; private set; }

        /// <summary>
        /// Texture sizes tried by <see cref="ProbeTextureSizes"/>, in order. Sizes above the last are not probed.
        /// </summary>
        private static readonly int[] probed_texture_sizes = { 2048, 4096, 8192 };

        private const int default_font_atlas_size = 4096;
        private const int max_single_channel_font_atlas_size = 8192;

        private int verifiedSingleChannelSize = default_font_atlas_size;

        /// <summary>
        /// The size the driver reports, before <see cref="ProbeTextureSizes"/> limits <see cref="MaxTextureSize"/> to verified RGBA sizes.
        /// </summary>
        private int? reportedMaxTextureSize;

        /// <summary>
        /// The size of glyph atlases. Single-channel atlases use a quarter of the memory, so they grow to the largest verified
        /// single-channel size (up to 8192), which keeps large glyph sets such as CJK text on fewer atlas pages.
        /// </summary>
        internal int FontAtlasSize => SingleChannelFontAtlas
            ? Math.Min(Math.Min(max_single_channel_font_atlas_size, verifiedSingleChannelSize), reportedMaxTextureSize ?? MaxTextureSize)
            : Math.Min(default_font_atlas_size, MaxTextureSize);

        /// <summary>
        /// Verifies, once per device and driver, which texture sizes can actually be allocated and rendered to, and limits
        /// <see cref="MaxTextureSize"/> to them. Some drivers report sizes that fail to allocate.
        /// Must run with the device ready, before resources are created.
        /// </summary>
        /// <param name="fingerprint">Identifies the device and driver.</param>
        /// <param name="tryAllocate">Allocates and clears a square render target of the given size and format, returning whether it succeeded.</param>
        internal void ProbeTextureSizes(string fingerprint, Func<int, TexturePixelFormat, bool> tryAllocate)
        {
            if (GraphicsProbeCache == null)
                return;

            int reported = MaxTextureSize;
            int conservative = Math.Min(reported, default_font_atlas_size);

            TextureSizeSupport = GraphicsProbeCache.GetOrProbe("texture-size", $"{GetType().Name}|{fingerprint}|{reported}",
                () => probeTextureSizes(reported, tryAllocate),
                saved => saved.MaxRgbaSize > 0 && saved.MaxSingleChannelSize > 0 && !string.IsNullOrEmpty(saved.Details),
                () => new TextureSizeProbeResult(conservative, conservative, false, "Previous probe did not finish; using conservative sizes. Clear graphics probe cache to retry."),
                reason => new TextureSizeProbeResult(conservative, conservative, false, reason),
                e => new TextureSizeProbeResult(conservative, conservative, false, $"Probe failed: {e.Message}"));

            reportedMaxTextureSize = reported;
            MaxTextureSize = TextureSizeSupport.CoversReportedSize ? reported : Math.Min(reported, TextureSizeSupport.MaxRgbaSize);
            verifiedSingleChannelSize = TextureSizeSupport.MaxSingleChannelSize;

            Logging.Logger.Log($"Texture size probe: reported {reported}, using {MaxTextureSize}; font atlas {FontAtlasSize}. {TextureSizeSupport.Details}");
        }

        private static TextureSizeProbeResult probeTextureSizes(int reported, Func<int, TexturePixelFormat, bool> tryAllocate)
        {
            int rgba = 0;
            bool allPassed = true;

            foreach (int size in probed_texture_sizes)
            {
                if (size > reported)
                    break;

                if (!tryAllocate(size, TexturePixelFormat.R8G8B8A8Float))
                {
                    allPassed = false;
                    break;
                }

                rgba = size;
            }

            // Single-channel textures are a quarter of the size, so they may fit where RGBA did not.
            int singleChannel = rgba;

            foreach (int size in probed_texture_sizes)
            {
                if (size <= singleChannel || size > reported)
                    continue;

                if (!tryAllocate(size, TexturePixelFormat.R8Float))
                    break;

                singleChannel = size;
            }

            if (rgba == 0)
            {
                // Even the smallest probe failed. Keep a size the framework cannot work without rather than failing outright.
                rgba = Math.Min(reported, 1024);
                singleChannel = Math.Max(singleChannel, rgba);
            }

            return new TextureSizeProbeResult(rgba, singleChannel, allPassed,
                $"Verified RGBA {rgba}, single-channel {singleChannel}{(allPassed ? string.Empty : " (RGBA probe stopped at a failure)")}.");
        }

        /// <summary>Cached compatibility evidence. This does not enable bindless rendering.</summary>
        public BindlessTextureProbeResult BindlessTextureSupport { get; private set; } =
            new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unknown, "Probe disabled or renderer not initialised.");

        internal void ProbeBindless(string fingerprint, Func<BindlessTextureProbeResult> probe)
        {
            if (GraphicsProbeCache == null || !ProbeBindlessTextures)
                return;
            try
            {
                BindlessTextureSupport = GraphicsProbeCache.GetOrProbe($"{GetType().Name}|{fingerprint}", probe);
            }
            catch (Exception e)
            {
                BindlessTextureSupport = new BindlessTextureProbeResult(BindlessTextureProbeStatus.Failed, e.Message);
            }
            Logging.Logger.Log($"Bindless texture probe: {BindlessTextureSupport.Status}. {BindlessTextureSupport.Details}");
        }
        internal Configuration.PresentationPreference PresentationPreference { get; set; }
        internal Configuration.VulkanPresentationPreference VulkanPresentationPreference { get; set; }
        internal bool RequestVulkanExclusiveFullscreen { get; set; }
        internal virtual bool ExclusiveFullscreenAllowed { get; set; }
        internal virtual double FullscreenRefreshRate { get; set; }

        /// <summary>
        /// The length of no usage (in frames) before freeing unused resources.
        /// </summary>
        internal const int RESOURCE_FREE_NO_USAGE_LENGTH = 300;

        protected internal abstract bool VerticalSync { get; set; }
        protected internal abstract bool AllowTearing { get; set; }

        protected internal Storage? CacheStorage
        {
            set => shaderCompilationStore.CacheStorage = value;
        }

        public int MaxTextureSize { get; protected set; } = 4096; // default value is to allow roughly normal flow in cases we don't have graphics context, like headless CI.

        public int MaxTexturesUploadedPerFrame { get; set; } = 32;
        public int MaxPixelsUploadedPerFrame { get; set; } = 1024 * 1024 * 2;

        public abstract bool IsDepthRangeZeroToOne { get; }
        public abstract bool IsUvOriginTopLeft { get; }
        public abstract bool IsClipSpaceYInverted { get; }

        public ulong FrameIndex { get; private set; }

        public ref readonly MaskingInfo CurrentMaskingInfo => ref currentMaskingInfo;

        public RectangleI Viewport { get; private set; }
        public RectangleI Scissor { get; private set; }
        public Vector2I ScissorOffset { get; private set; }
        public Matrix4 ProjectionMatrix { get; private set; }
        public DepthInfo CurrentDepthInfo { get; private set; }
        public StencilInfo CurrentStencilInfo { get; private set; }
        public WrapMode CurrentWrapModeS { get; private set; }
        public WrapMode CurrentWrapModeT { get; private set; }
        public bool IsMaskingActive { get; private set; }
        public bool UsingBackbuffer { get; private set; }
        public Texture WhitePixel => whitePixel.Value;
        DepthValue IRenderer.BackbufferDepth => backBufferDepth;

        // Window surfaces request a depth buffer on every backend.
        bool IRenderer.TargetHasDepth => FrameBuffer?.HasDepth ?? true;

        public bool IsInitialised { get; private set; }

        protected ClearInfo CurrentClearInfo { get; private set; }
        public BlendingParameters CurrentBlendingParameters { get; private set; }
        protected BlendingMask CurrentBlendingMask { get; private set; }

        /// <summary>
        /// Whether scissor is currently enabled.
        /// </summary>
        protected bool ScissorState { get; private set; }

        /// <summary>
        /// The current framebuffer, or null if the backbuffer is used.
        /// </summary>
        protected IFrameBuffer? FrameBuffer { get; private set; }

        /// <summary>
        /// The current shader, or null if no shader is currently bound.
        /// </summary>
        protected IShader? Shader { get; private set; }

        private readonly ShaderCompilationStore shaderCompilationStore = new ShaderCompilationStore();

        private readonly GlobalStatistic<int> statExpensiveOperationsQueued;
        private readonly GlobalStatistic<int> statTextureUploadsQueued;
        private readonly GlobalStatistic<int> statTextureUploadsDequeued;
        private readonly GlobalStatistic<int> statTextureUploadsPerformed;
        private readonly GlobalStatistic<int> vboInUse;

        private readonly ConcurrentQueue<ScheduledDelegate> expensiveOperationQueue = new ConcurrentQueue<ScheduledDelegate>();

        private static readonly long expensive_operation_budget_ticks = Stopwatch.Frequency * 2 / 1000;
        private readonly Queue<INativeTexture> textureUploadQueue = new Queue<INativeTexture>();

        // Both collections are protected by textureUploadQueue; membership tracks queued entries, not in-progress uploads.
        private readonly HashSet<INativeTexture> queuedTexturesForUpload = new HashSet<INativeTexture>(ReferenceEqualityComparer.Instance);

        private readonly RendererDisposalQueue disposalQueue = new RendererDisposalQueue();

        private readonly Scheduler resetScheduler = new Scheduler(() => ThreadSafety.IsDrawThread, new StopwatchClock(true)); // force no thread set until we are actually on the draw thread.

        private readonly DepthValue backBufferDepth = new DepthValue();

        private readonly Dictionary<string, IUniformBuffer> boundUniformBuffers = new Dictionary<string, IUniformBuffer>();
        private readonly Stack<IVertexBatch<TexturedVertex2D>> quadBatches = new Stack<IVertexBatch<TexturedVertex2D>>();
        private readonly List<IVertexBuffer> vertexBuffersInUse = new List<IVertexBuffer>();
        private readonly List<IVertexBatch> batchResetList = new List<IVertexBatch>();
        private readonly Stack<RectangleI> viewportStack = new Stack<RectangleI>();
        private readonly Stack<Matrix4> projectionMatrixStack = new Stack<Matrix4>();
        private readonly Stack<MaskingInfo> maskingStack = new Stack<MaskingInfo>();
        private readonly Stack<RectangleI> scissorRectStack = new Stack<RectangleI>();
        private readonly Stack<DepthInfo> depthStack = new Stack<DepthInfo>();
        private readonly Stack<StencilInfo> stencilStack = new Stack<StencilInfo>();
        private readonly Stack<Vector2I> scissorOffsetStack = new Stack<Vector2I>();
        private readonly Stack<IFrameBuffer> frameBufferStack = new Stack<IFrameBuffer>();
        private readonly Stack<IShader> shaderStack = new Stack<IShader>();
        private readonly Stack<bool> scissorStateStack = new Stack<bool>();

        private readonly INativeTexture?[] lastBoundTexture = new INativeTexture?[16];
        private readonly bool[] lastBoundTextureIsAtlas = new bool[16];

        /// <summary>
        /// The number of texture units the default texture shader samples, choosing one per vertex (see <c>sh_Texture.fs</c>).
        /// Must match the shader and the packing in <see cref="StampMaskingIndex{T}(ref T)"/>.
        /// </summary>
        internal const int TEXTURE_SLOTS = 4;

        /// <summary>
        /// Whether the default texture shader may draw textures from several units in one batch.
        /// Initialised from the TextureSlots framework setting at startup.
        /// </summary>
        internal static bool TextureSlotsEnabled { get; set; } = true;

        /// <summary>
        /// Whether this renderer binds textures to several slots for the default texture shader (see <see cref="TextureSlotsEnabled"/>).
        /// When false, every texture is bound to unit 0 and the other slots hold the white pixel's texture.
        /// </summary>
        protected virtual bool BindsTextureSlots => true;

        /// <summary>
        /// Whether texture shaders should be compiled to sample several slots. Shaders declaring slots that are never bound leave their
        /// resource layouts unset, which Vulkan cannot create a pipeline from.
        /// </summary>
        internal bool UsesTextureSlotShaders => TextureSlotsEnabled && BindsTextureSlots;

        /// <summary>
        /// Whether the bound shader is a default texture shader (see <see cref="defaultTextureShaders"/>), cached since vertices consult it.
        /// </summary>
        private bool defaultTextureShaderActive;

        /// <summary>
        /// The unit holding the texture that vertices added now sample. Always 0 unless the default texture shader is bound.
        /// </summary>
        private int currentTextureSlot;

        /// <summary>
        /// Bit <c>i</c> is set when a vertex sampling unit <c>i</c> was added since the last draw, so that unit cannot be rebound without a flush.
        /// </summary>
        private int textureSlotsUsedInBatch;

        /// <summary>
        /// The unit replaced most recently when no unit was free, so replacements rotate through units.
        /// </summary>
        private int lastReplacedTextureSlot;

        /// <summary>
        /// Bit <c>i</c> is set when the texture bound to unit <c>i</c> is single-channel, as last given to <see cref="GlobalUniformData.SingleChannelSlots"/>.
        /// Unlike <see cref="lastBoundTexture"/>, this is not reset between frames, since the uniform keeps its value.
        /// </summary>
        private int singleChannelSlots;

        /// <summary>
        /// Bit <c>i</c> is set when the texture bound to unit <c>i</c> is sampled with nearest filtering, as last given to
        /// <see cref="GlobalUniformData.NearestSlots"/>. Only bindless texture tables read it, since they share samplers.
        /// </summary>
        private int nearestSlots;

        /// <summary>
        /// The number of slots the default texture shader samples. Must match <c>G0V0_TEXTURE_SLOT_COUNT</c> in its shaders, and at most 16.
        /// </summary>
        protected virtual int TextureSlotCount => TEXTURE_SLOTS;

        /// <summary>
        /// The texture last bound to a unit (slot), as tracked for batching.
        /// </summary>
        protected INativeTexture? GetBoundTexture(int unit) => lastBoundTexture[unit];

        /// <summary>
        /// Whether a texture is sampled with nearest filtering, for texture tables that share samplers.
        /// </summary>
        protected virtual bool UsesNearestFiltering(INativeTexture texture) => false;

        /// <summary>
        /// Called around draws with the default texture shader, for renderers that bind its slots differently from other shaders' units.
        /// </summary>
        protected virtual void BeginTextureSlotDraw()
        {
        }

        /// <inheritdoc cref="BeginTextureSlotDraw"/>
        protected virtual void EndTextureSlotDraw()
        {
        }

        // in case no other textures are used in the project, create a new atlas as a fallback source for the white pixel area (used to draw boxes etc.)
        private readonly Lazy<TextureWhitePixel> whitePixel;
        private readonly LockedWeakList<Texture> allTextures = new LockedWeakList<Texture>();

        private IUniformBuffer<GlobalUniformData>? globalUniformBuffer;
        private IVertexBatch<TexturedVertex2D>? defaultQuadBatch;
        private IVertexBatch? currentActiveBatch;
        private MaskingInfo currentMaskingInfo;
        private MaskingInfo appliedMaskingInfo;
        private bool appliedMaskingActive;
        private RectangleI? appliedScissor;
        private bool appliedScissorEnabled;
        private RectangleI? appliedHardwareScissor;
        private bool appliedClipsInShader;
        private bool maskingStatePending;
        private int lastActiveTextureUnit;
        private bool globalUniformsChanged;

        private static readonly GlobalStatistic<int>[] flush_source_statistics;

        static Renderer()
        {
            var sources = Enum.GetValues<FlushBatchSource>();

            flush_source_statistics = new GlobalStatistic<int>[sources.Length];
            foreach (FlushBatchSource source in sources)
                flush_source_statistics[(int)source] = GlobalStatistics.Get<int>(nameof(FlushBatchSource), source.ToString());
        }

        protected Renderer()
        {
            statTextureUploadsPerformed = GlobalStatistics.Get<int>(GetType().Name, "Texture uploads performed");
            statTextureUploadsDequeued = GlobalStatistics.Get<int>(GetType().Name, "Texture uploads dequeued");
            statTextureUploadsQueued = GlobalStatistics.Get<int>(GetType().Name, "Texture upload queue length");
            statExpensiveOperationsQueued = GlobalStatistics.Get<int>(GetType().Name, "Expensive operation queue length");
            vboInUse = GlobalStatistics.Get<int>(GetType().Name, "VBOs in use");

            whitePixel = new Lazy<TextureWhitePixel>(() =>
                new TextureAtlas(this, TextureAtlas.WHITE_PIXEL_SIZE + TextureAtlas.PADDING, TextureAtlas.WHITE_PIXEL_SIZE + TextureAtlas.PADDING, true).WhitePixel);
        }

        void IRenderer.Initialise(IGraphicsSurface graphicsSurface)
        {
            switch (graphicsSurface.Type)
            {
                case GraphicsSurfaceType.OpenGL:
                    Trace.Assert(graphicsSurface is IOpenGLGraphicsSurface, $"Window must implement {nameof(IOpenGLGraphicsSurface)}.");
                    break;

                case GraphicsSurfaceType.Metal:
                    Trace.Assert(graphicsSurface is IMetalGraphicsSurface, $"Window graphics API must implement {nameof(IMetalGraphicsSurface)}.");
                    break;
            }

            Initialise(graphicsSurface);
            CompleteInitialisation();
        }

        protected void CompleteInitialisation()
        {
            defaultQuadBatch = CreateQuadBatch<TexturedVertex2D>(100, 1000);
            resetScheduler.AddDelayed(disposalQueue.CheckPendingDisposals, 0, true);

            IsInitialised = true;
        }

        /// <summary>
        /// Resets any states to prepare for drawing a new frame.
        /// </summary>
        /// <param name="windowSize">The full window size.</param>
        protected internal virtual void BeginFrame(Vector2 windowSize)
        {
            foreach (var source in flush_source_statistics)
                source.Value = 0;

            globalUniformBuffer ??= ((IRenderer)this).CreateUniformBuffer<GlobalUniformData>();

            Debug.Assert(defaultQuadBatch != null);

            FrameIndex++;
            drawCapture?.Begin(FrameIndex, windowSize, GetType().Name);

            backBufferDepth.Reset();

            resetScheduler.Update();

            statExpensiveOperationsQueued.Value = expensiveOperationQueue.Count;

            // Run at least one pending operation, then continue while within a small budget. With cached shader binaries most
            // operations are short, and draining several per frame lets loading screens finish shader initialisation before
            // anything binds those shaders (which would otherwise initialise them inline, mid-frame).
            long budgetEnd = Stopwatch.GetTimestamp() + expensive_operation_budget_ticks;

            while (expensiveOperationQueue.TryDequeue(out ScheduledDelegate? operation))
            {
                if (operation.State != ScheduledDelegate.RunState.Waiting)
                    continue;

                operation.RunTask();

                if (Stopwatch.GetTimestamp() >= budgetEnd)
                    break;
            }

            globalUniformsChanged = true;
            currentActiveBatch = null;
            CurrentBlendingParameters = new BlendingParameters();
            currentMaskingInfo = default;
            appliedMaskingInfo = default;
            appliedMaskingActive = false;
            invalidateAppliedScissor();
            maskingBufferIndex = 0;
            nextMaskingEntry = 0;
            appliedMaskingEntry = -1;

            foreach (var b in batchResetList)
                b.ResetCounters();
            batchResetList.Clear();

            Shader?.Unbind();
            Shader = null;
            defaultTextureShaderActive = false;
            currentTextureSlot = 0;
            textureSlotsUsedInBatch = 0;

            boundUniformBuffers.Clear();
            viewportStack.Clear();
            projectionMatrixStack.Clear();
            maskingStack.Clear();
            scissorRectStack.Clear();
            frameBufferStack.Clear();
            depthStack.Clear();
            stencilStack.Clear();
            scissorStateStack.Clear();
            scissorOffsetStack.Clear();
            shaderStack.Clear();

            quadBatches.Clear();
            quadBatches.Push(defaultQuadBatch);

            setFrameBuffer(null, true);

            Scissor = RectangleI.Empty;
            ScissorOffset = Vector2I.Zero;
            Viewport = RectangleI.Empty;
            ProjectionMatrix = Matrix4.Identity;

            PushScissorState(true);
            PushViewport(new RectangleI(0, 0, (int)windowSize.X, (int)windowSize.Y));
            PushScissor(new RectangleI(0, 0, (int)windowSize.X, (int)windowSize.Y));
            PushScissorOffset(Vector2I.Zero);
            PushMaskingInfo(new MaskingInfo
            {
                ScreenSpaceAABB = new RectangleI(0, 0, (int)windowSize.X, (int)windowSize.Y),
                MaskingRect = new RectangleF(0, 0, windowSize.X, windowSize.Y),
                ToMaskingSpace = Matrix3.Identity,
                BlendRange = 1,
                AlphaExponent = 1,
                CornerExponent = 2.5f,
            }, true);

            PushDepthInfo(DepthInfo.Default);
            PushStencilInfo(StencilInfo.Default);

            Clear(new ClearInfo(Color4.Black));

            freeUnusedVertexBuffers();
            vboInUse.Value = vertexBuffersInUse.Count;

            int queuedUploadCount;

            lock (textureUploadQueue)
                queuedUploadCount = textureUploadQueue.Count;

            statTextureUploadsQueued.Value = queuedUploadCount;
            statTextureUploadsDequeued.Value = 0;
            statTextureUploadsPerformed.Value = 0;

            // increase the number of items processed with the queue length to ensure it doesn't get out of hand.
            int targetUploads = Math.Clamp(queuedUploadCount / 2, 1, MaxTexturesUploadedPerFrame);
            int uploads = 0;
            int uploadedPixels = 0;

            // continue attempting to upload textures until enough uploads have been performed.
            while (tryDequeueTextureUpload(out INativeTexture? texture))
            {
                statTextureUploadsDequeued.Value++;

                if (!texture.Upload())
                    continue;

                statTextureUploadsPerformed.Value++;

                if (++uploads >= targetUploads)
                    break;

                if ((uploadedPixels += texture.Width * texture.Height) > MaxPixelsUploadedPerFrame)
                    break;
            }

            lastBoundTexture.AsSpan().Clear();
            lastBoundTextureIsAtlas.AsSpan().Clear();
            textureBoundForCurrentFrameBuffer = false;
        }

        /// <summary>
        /// Performs any last actions before a frame ends.
        /// </summary>
        protected internal virtual void FinishFrame()
        {
            FlushCurrentBatch(FlushBatchSource.FinishFrame);
            drawCapture?.Finish();
        }

        public void ScheduleExpensiveOperation(ScheduledDelegate operation)
        {
            if (IsInitialised)
                expensiveOperationQueue.Enqueue(operation);
        }

        protected void DrainDisposalsAfterGpuIdle() => disposalQueue.Drain();

        public void ScheduleDisposal<T>(Action<T> disposalAction, T target)
        {
            if (IsInitialised)
                disposalQueue.ScheduleDisposal(disposalAction, target);
            else
                disposalAction.Invoke(target);
        }

        /// <summary>
        /// Returns an image containing the current content of the backbuffer, i.e. takes a screenshot.
        /// </summary>
        protected internal abstract Image<Rgba32> TakeScreenshot();

        /// <summary>
        /// Returns an image containing the content of a framebuffer.
        /// </summary>
        protected internal virtual Image<Rgba32>? ExtractFrameBufferData(IFrameBuffer frameBuffer) => null;

        /// <summary>
        /// Performs a once-off initialisation of this <see cref="Renderer"/>.
        /// </summary>
        protected abstract void Initialise(IGraphicsSurface graphicsSurface);

        /// <summary>
        /// Swaps the back buffer with the front buffer to display the new frame.
        /// </summary>
        protected internal abstract void SwapBuffers();

        /// <summary>
        /// Waits until all renderer commands have been fully executed GPU-side, as signaled by the graphics backend.
        /// </summary>
        /// <remarks>
        /// This is equivalent to a <c>glFinish</c> call.
        /// </remarks>
        protected internal abstract void WaitUntilIdle();

        protected internal abstract void WaitUntilNextFrameReady();

        /// <summary>
        /// Invoked when the rendering thread is active and commands will be enqueued.
        /// This is mainly required for OpenGL renderers to mark context as current before performing GL calls.
        /// </summary>
        protected internal abstract void MakeCurrent();

        /// <summary>
        /// Invoked when the rendering thread is suspended and no more commands will be enqueued.
        /// This is mainly required for OpenGL renderers to mark context as current before performing GL calls.
        /// </summary>
        protected internal abstract void ClearCurrent();

        #region Clear

        public void Clear(ClearInfo clearInfo)
        {
            PushDepthInfo(new DepthInfo(writeDepth: true));
            PushScissorState(false);

            ClearImplementation(clearInfo);

            CurrentClearInfo = clearInfo;

            PopScissorState();
            PopDepthInfo();
        }

        /// <summary>
        /// Informs the graphics device to clear the color and depth targets of the currently bound framebuffer.
        /// </summary>
        /// <param name="clearInfo">The clear parameters.</param>
        protected abstract void ClearImplementation(ClearInfo clearInfo);

        #endregion

        #region Blending

        public void SetBlend(BlendingParameters blendingParameters)
        {
            if (CurrentBlendingParameters == blendingParameters)
                return;

            FlushCurrentBatch(FlushBatchSource.SetBlend);
            SetBlendImplementation(blendingParameters);

            CurrentBlendingParameters = blendingParameters;
        }

        public void SetBlendMask(BlendingMask blendingMask)
        {
            if (CurrentBlendingMask == blendingMask)
                return;

            FlushCurrentBatch(FlushBatchSource.SetBlendMask);
            SetBlendMaskImplementation(blendingMask);

            CurrentBlendingMask = blendingMask;
        }

        /// <summary>
        /// Updates the graphics device with the new blending parameters.
        /// </summary>
        /// <param name="blendingParameters">The blending parameters.</param>
        protected abstract void SetBlendImplementation(BlendingParameters blendingParameters);

        /// <summary>
        /// Updates the graphics device with the new blending mask.
        /// </summary>
        /// <param name="blendingMask">The blending mask.</param>
        protected abstract void SetBlendMaskImplementation(BlendingMask blendingMask);

        #endregion

        #region Viewport

        public void PushViewport(RectangleI viewport)
        {
            var actualRect = viewport;

            if (actualRect.Width < 0)
            {
                actualRect.X += viewport.Width;
                actualRect.Width = -viewport.Width;
            }

            if (actualRect.Height < 0)
            {
                actualRect.Y += viewport.Height;
                actualRect.Height = -viewport.Height;
            }

            this.PushOrtho(viewport);

            viewportStack.Push(actualRect);
            setViewport(viewport);
        }

        public void PopViewport()
        {
            Trace.Assert(viewportStack.Count > 1);

            PopProjectionMatrix();

            viewportStack.Pop();
            setViewport(viewportStack.Peek());
        }

        private void setViewport(RectangleI viewport)
        {
            if (Viewport == viewport)
                return;

            FlushCurrentBatch(FlushBatchSource.SetScissor);
            SetViewportImplementation(viewport);
            Viewport = viewport;
            invalidateAppliedScissor();
        }

        /// <summary>
        /// Updates the graphics device with a new viewport rectangle.
        /// </summary>
        /// <param name="viewport">The viewport to use.</param>
        protected abstract void SetViewportImplementation(RectangleI viewport);

        #endregion

        #region Scissor

        public void PushScissor(RectangleI scissor)
        {
            scissorRectStack.Push(scissor);
            setScissor(scissor);
        }

        public void PushScissorState(bool enabled)
        {
            scissorStateStack.Push(enabled);
            setScissorState(enabled);
        }

        public void PushScissorOffset(Vector2I offset)
        {
            scissorOffsetStack.Push(offset);
            setScissorOffset(offset);
        }

        public void PopScissor()
        {
            Trace.Assert(scissorRectStack.Count > 1);

            scissorRectStack.Pop();
            setScissor(scissorRectStack.Peek());
        }

        public void PopScissorState()
        {
            Trace.Assert(scissorStateStack.Count > 1);

            scissorStateStack.Pop();
            setScissorState(scissorStateStack.Peek());
        }

        public void PopScissorOffset()
        {
            Trace.Assert(scissorOffsetStack.Count > 1);

            scissorOffsetStack.Pop();
            setScissorOffset(scissorOffsetStack.Peek());
        }

        private void setScissor(RectangleI scissor)
        {
            if (scissor.Width < 0)
            {
                scissor.X += scissor.Width;
                scissor.Width = -scissor.Width;
            }

            if (scissor.Height < 0)
            {
                scissor.Y += scissor.Height;
                scissor.Height = -scissor.Height;
            }

            if (Scissor == scissor)
                return;

            Scissor = scissor;
            maskingStatePending = true;
        }

        private void setScissorState(bool enabled)
        {
            if (enabled == ScissorState)
                return;

            FlushCurrentBatch(FlushBatchSource.SetScissor);
            SetScissorStateImplementation(enabled);
            ScissorState = enabled;

            // Shader-side clipping follows the scissor state.
            maskingStatePending = true;
        }

        private void setScissorOffset(Vector2I offset)
        {
            if (ScissorOffset == offset)
                return;

            FlushCurrentBatch(FlushBatchSource.SetScissor);
            ScissorOffset = offset;
        }

        /// <summary>
        /// Updates the graphics device with a new scissor rectangle.
        /// </summary>
        /// <param name="scissor">The scissor rectangle to use.</param>
        protected abstract void SetScissorImplementation(RectangleI scissor);

        /// <summary>
        /// Updates the graphics device with the new scissor state.
        /// </summary>
        /// <param name="enabled">Whether scissor should be enabled.</param>
        protected abstract void SetScissorStateImplementation(bool enabled);

        #endregion

        #region Projection Matrix

        public void PushProjectionMatrix(Matrix4 matrix)
        {
            projectionMatrixStack.Push(matrix);
            setProjectionMatrix(matrix);
        }

        public void PopProjectionMatrix()
        {
            Trace.Assert(projectionMatrixStack.Count > 1);

            projectionMatrixStack.Pop();
            setProjectionMatrix(projectionMatrixStack.Peek());
        }

        private void setProjectionMatrix(Matrix4 matrix)
        {
            if (ProjectionMatrix == matrix)
                return;

            FlushCurrentBatch(FlushBatchSource.SetProjection);

            globalUniformsChanged = true;
            ProjectionMatrix = matrix;
        }

        #endregion

        #region Masking

        public void PushMaskingInfo(in MaskingInfo maskingInfo, bool overwritePreviousScissor = false)
        {
            maskingStack.Push(maskingInfo);
            setMaskingInfo(maskingInfo, true, overwritePreviousScissor);
        }

        public void PopMaskingInfo()
        {
            Trace.Assert(maskingStack.Count > 1);

            maskingStack.Pop();
            setMaskingInfo(maskingStack.Peek(), false, true);
        }

        private void setMaskingInfo(MaskingInfo maskingInfo, bool isPushing, bool overwritePreviousScissor)
        {
            if (isPushing)
            {
                // When drawing to a viewport that doesn't match the projection size (e.g. via framebuffers), the resultant image will be scaled
                Vector2 projectionScale = new Vector2(ProjectionMatrix.Row0.X / 2, -ProjectionMatrix.Row1.Y / 2);
                Vector2 viewportScale = Vector2.Multiply(Viewport.Size, projectionScale);

                Vector2 location = (maskingInfo.ScreenSpaceAABB.Location - ScissorOffset) * viewportScale;
                Vector2 size = maskingInfo.ScreenSpaceAABB.Size * viewportScale;

                RectangleI actualRect = new RectangleI(
                    (int)Math.Floor(location.X),
                    (int)Math.Floor(location.Y),
                    (int)Math.Ceiling(size.X),
                    (int)Math.Ceiling(size.Y));

                PushScissor(overwritePreviousScissor ? actualRect : RectangleI.Intersect(scissorRectStack.Peek(), actualRect));
            }
            else
                PopScissor();

            currentMaskingInfo = maskingInfo;

            // Masking is enabled for as long as any masking info is active that's not the default.
            IsMaskingActive = maskingStack.Count > 1;
            maskingStatePending = true;
        }

        /// <summary>
        /// Applies the logical clipping state before new geometry is submitted. Intermediate
        /// push/pop transitions without geometry must not split otherwise compatible batches.
        /// </summary>
        /// <param name="batch">The batch that geometry is about to be added to, or null for direct draws.</param>
        internal void ApplyPendingMaskingState(IVertexBatch? batch = null)
        {
            if (!maskingStatePending || retainAppliedMaskingForQuad)
                return;

            var scissor = getEffectiveScissor();
            bool clipInShader = canClipInShader(batch);
            RectangleI hardwareScissor = clipInShader ? getViewportScissor() : scissor;

            bool maskChanged = appliedMaskingActive != IsMaskingActive
                               || !appliedMaskingInfo.HasSameShaderParameters(currentMaskingInfo);
            bool scissorChanged = appliedScissor != scissor || appliedScissorEnabled != ScissorState;
            bool hardwareScissorChanged = appliedHardwareScissor != hardwareScissor;

            // Geometry clipped in shaders carries its masking buffer entry, so only the hardware scissor affects it.
            // Other geometry reads global uniforms and the hardware scissor, which must not change under it.
            if (hardwareScissorChanged || ((maskChanged || scissorChanged) && !appliedClipsInShader))
                FlushCurrentBatch(maskChanged ? FlushBatchSource.SetMasking : FlushBatchSource.SetScissor);

            appliedMaskingInfo = currentMaskingInfo;
            appliedMaskingActive = IsMaskingActive;
            appliedScissor = scissor;
            appliedScissorEnabled = ScissorState;

            if (maskChanged || scissorChanged || appliedMaskingEntry < 0)
            {
                appendMaskingEntry();
                globalUniformsChanged = true;
            }

            if (hardwareScissorChanged)
            {
                SetScissorImplementation(hardwareScissor);
                appliedHardwareScissor = hardwareScissor;
            }

            appliedClipsInShader = clipInShader;
            maskingStatePending = false;
        }

        private void invalidateAppliedScissor()
        {
            appliedScissor = null;
            appliedHardwareScissor = null;
            maskingStatePending = true;
        }

        /// <summary>
        /// Whether geometry about to be added to <paramref name="batch"/> can be masked and clipped by the bound shader alone, leaving
        /// the hardware scissor at the whole viewport. This needs vertices that carry a masking buffer entry, a shader that reads it
        /// (see <see cref="PerVertexMasking"/>), and no depth or stencil writes, which shader clipping would not prevent.
        /// </summary>
        private bool canClipInShader(IVertexBatch? batch)
            => batch is IVertexBatch<TexturedVertex2D>
               && Shader is IPerVertexMaskingShader { UsesPerVertexMasking: true }
               && !CurrentDepthInfo.WriteDepth
               && !CurrentStencilInfo.StencilTest;

        /// <summary>
        /// The hardware scissor covering the whole viewport, in the same space as <see cref="getEffectiveScissor"/>.
        /// </summary>
        private RectangleI getViewportScissor()
        {
            var scissor = Viewport;
            if (!UsingBackbuffer && !IsUvOriginTopLeft)
                scissor.Y = Viewport.Height - scissor.Bottom;
            return scissor.Intersect(Viewport);
        }

        #region Masking buffer

        // Must match the uniform-block fallback length in sh_GlobalUniforms.h.
        private const int masking_buffer_ubo_size = 80;
        private const int masking_buffer_ssbo_size = 4096;
        private const string masking_buffer_name = "g_MaskingBuffer";

        private readonly List<IShaderStorageBufferObject<MaskingData>> maskingBuffers = new List<IShaderStorageBufferObject<MaskingData>>();
        private IUniformBuffer? boundMaskingBuffer;
        private int maskingBufferIndex;
        private int nextMaskingEntry;

        /// <summary>
        /// The applied state's entry in the current masking buffer. Entries are only appended during a frame, so geometry keeps
        /// referring to the state it was submitted with.
        /// </summary>
        private int appliedMaskingEntry = -1;

        private void appendMaskingEntry()
        {
            if (maskingBuffers.Count == 0)
                maskingBuffers.Add(((IRenderer)this).CreateShaderStorageBufferObject<MaskingData>(masking_buffer_ubo_size, masking_buffer_ssbo_size));

            if (nextMaskingEntry >= maskingBuffers[maskingBufferIndex].Size)
            {
                // Pending geometry refers to entries in the current buffer.
                FlushCurrentBatch(FlushBatchSource.StorageBufferOverflow);

                maskingBufferIndex++;
                nextMaskingEntry = 0;

                if (maskingBufferIndex == maskingBuffers.Count)
                    maskingBuffers.Add(((IRenderer)this).CreateShaderStorageBufferObject<MaskingData>(masking_buffer_ubo_size, masking_buffer_ssbo_size));
            }

            var info = appliedMaskingInfo;

            maskingBuffers[maskingBufferIndex][nextMaskingEntry] = new MaskingData
            {
                ToMaskingSpace = info.ToMaskingSpace,
                MaskingRect = new Vector4(info.MaskingRect.Left, info.MaskingRect.Top, info.MaskingRect.Right, info.MaskingRect.Bottom),
                ScissorRect = getScreenSpaceScissor(),
                BorderColour = info.BorderThickness > 0
                    ? new Matrix4(
                        info.BorderColour.TopLeft.SRGB.R, info.BorderColour.TopLeft.SRGB.G, info.BorderColour.TopLeft.SRGB.B, info.BorderColour.TopLeft.SRGB.A,
                        info.BorderColour.BottomLeft.SRGB.R, info.BorderColour.BottomLeft.SRGB.G, info.BorderColour.BottomLeft.SRGB.B, info.BorderColour.BottomLeft.SRGB.A,
                        info.BorderColour.TopRight.SRGB.R, info.BorderColour.TopRight.SRGB.G, info.BorderColour.TopRight.SRGB.B, info.BorderColour.TopRight.SRGB.A,
                        info.BorderColour.BottomRight.SRGB.R, info.BorderColour.BottomRight.SRGB.G, info.BorderColour.BottomRight.SRGB.B, info.BorderColour.BottomRight.SRGB.A)
                    : default,
                CornerRadius = info.CornerRadius,
                CornerExponent = info.CornerExponent,
                BorderThickness = info.BorderThickness / info.BlendRange,
                MaskingBlendRange = info.BlendRange,
                AlphaExponent = info.AlphaExponent,
                InnerCornerRadius = info.Hollow ? info.HollowCornerRadius : 0,
                EdgeOffset = info.EdgeOffset,
                IsMasking = appliedMaskingActive,
                DiscardInner = info.Hollow,
            };

            appliedMaskingEntry = nextMaskingEntry++;
        }

        /// <summary>
        /// The applied scissor in the space of vertex positions, for clipping in shaders. This inverts the mapping used for masking
        /// AABBs in <see cref="setMaskingInfo"/>, so a pixel centre passes exactly when it lies inside the hardware scissor.
        /// </summary>
        private Vector4 getScreenSpaceScissor()
        {
            if (!ScissorState)
                return new Vector4(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue);

            Vector2 projectionScale = new Vector2(ProjectionMatrix.Row0.X / 2, -ProjectionMatrix.Row1.Y / 2);
            Vector2 viewportScale = Vector2.Multiply(Viewport.Size, projectionScale);

            if (viewportScale.X == 0 || viewportScale.Y == 0)
                return new Vector4(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue);

            RectangleI scissor = Scissor.Intersect(Viewport);

            Vector2 a = Vector2.Divide(new Vector2(scissor.Left, scissor.Top), viewportScale) + ScissorOffset;
            Vector2 b = Vector2.Divide(new Vector2(scissor.Right, scissor.Bottom), viewportScale) + ScissorOffset;

            return new Vector4(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        }

        /// <summary>
        /// Sets a vertex's masking buffer entry to the applied state's, for vertex types that carry one.
        /// </summary>
        internal void StampMaskingIndex<T>(ref T vertex)
            where T : unmanaged
        {
            if (typeof(T) == typeof(TexturedVertex2D))
            {
                Unsafe.As<T, TexturedVertex2D>(ref vertex).MaskingIndex = (appliedMaskingEntry + 1) * TextureSlotCount + currentTextureSlot;
                textureSlotsUsedInBatch |= 1 << currentTextureSlot;
            }
        }

        /// <summary>
        /// Sets the masking buffer entry of each vertex, for vertex types that carry one.
        /// </summary>
        /// <returns><paramref name="vertices"/>, or a stamped copy of them in <paramref name="scratch"/>.</returns>
        internal ReadOnlySpan<T> StampMaskingIndex<T>(ReadOnlySpan<T> vertices, Span<T> scratch)
            where T : unmanaged
        {
            if (typeof(T) != typeof(TexturedVertex2D))
                return vertices;

            vertices.CopyTo(scratch);
            scratch = scratch[..vertices.Length];

            for (int i = 0; i < scratch.Length; i++)
                StampMaskingIndex(ref scratch[i]);

            return scratch;
        }

        #endregion

        private RectangleI getEffectiveScissor()
        {
            // Match the framebuffer UV-origin compensation used by the vertex shader.
            var scissor = Scissor;
            if (!UsingBackbuffer && !IsUvOriginTopLeft)
                scissor.Y = Viewport.Height - scissor.Bottom;
            return scissor.Intersect(Viewport);
        }

        /// <summary>
        /// Masking-space margin added to the unaffected interior, absorbing CPU/GPU transform rounding.
        /// </summary>
        private const float masking_interior_margin = 1;

        /// <summary>
        /// Instances of the framework's default <c>Texture2D/Texture</c> shader. Its output depends on clipping state only through
        /// <c>getRoundedColor()</c> in <c>sh_Masking.h</c>, and it supports untextured quads (see <see cref="CanDrawUntextured"/>).
        /// </summary>
        private readonly ConditionalWeakTable<IShader, object?> defaultTextureShaders = new ConditionalWeakTable<IShader, object?>();

        private bool isDefaultTextureShaderBound => defaultTextureShaderActive;

        private bool retainAppliedMaskingForQuad;

        /// <summary>
        /// Allows a quad to join the current batch under the applied clipping state instead of applying a pending one.
        /// <para>
        /// Inside the unaffected interior of a mask (clear of rounded corners, borders and the blend range) and inside its
        /// scissor, <c>getRoundedColor()</c> reduces to the content colour, attenuated only by the quad's own vertex blend range
        /// raised to the mask's alpha exponent. If a quad lies in that interior for both the applied and the pending state,
        /// and the alpha exponents agree, the two states are interchangeable for it. This is common for text and icons within
        /// nested rounded containers, where every container otherwise splits the batch twice.
        /// </para>
        /// </summary>
        /// <returns>Whether the applied state was retained. If so, <see cref="EndRetainAppliedMasking"/> must follow the submission.</returns>
        internal bool TryRetainAppliedMasking(object batch, in Quad quad, Vector2 blendRange)
        {
            if (!maskingStatePending || appliedScissor is not RectangleI applied)
                return false;

            // Geometry masked in shaders does not split batches anyway, and retaining would bypass switching between hardware
            // and shader clipping.
            if (appliedClipsInShader || canClipInShader(batch as IVertexBatch))
                return false;

            if (!isDefaultTextureShaderBound)
                return false;

            // Without a vertex blend range, unmasked drawing returns the content colour directly. With one, the full masking
            // path runs even when unmasked (against the root masking info), so the interior test applies to both states.
            bool vertexBlend = blendRange != Vector2.Zero;

            if (vertexBlend && appliedMaskingInfo.AlphaExponent != currentMaskingInfo.AlphaExponent)
                return false;

            if (!isInsideScissor(quad, applied) || !isInsideScissor(quad, getEffectiveScissor()))
                return false;

            if (!isInsideMaskingInterior(quad, appliedMaskingInfo, appliedMaskingActive || vertexBlend)
                || !isInsideMaskingInterior(quad, currentMaskingInfo, IsMaskingActive || vertexBlend))
                return false;

            retainAppliedMaskingForQuad = true;
            return true;
        }

        internal void EndRetainAppliedMasking() => retainAppliedMaskingForQuad = false;

        private bool isInsideScissor(in Quad quad, RectangleI scissor)
        {
            // Same mapping as used for masking AABBs in setMaskingInfo() and getEffectiveScissor().
            Vector2 projectionScale = new Vector2(ProjectionMatrix.Row0.X / 2, -ProjectionMatrix.Row1.Y / 2);
            Vector2 viewportScale = Vector2.Multiply(Viewport.Size, projectionScale);

            RectangleF aabb = quad.AABBFloat;
            Vector2 topLeft = (aabb.TopLeft - ScissorOffset) * viewportScale;
            Vector2 bottomRight = (aabb.BottomRight - ScissorOffset) * viewportScale;

            float left = MathF.Min(topLeft.X, bottomRight.X);
            float right = MathF.Max(topLeft.X, bottomRight.X);
            float top = MathF.Min(topLeft.Y, bottomRight.Y);
            float bottom = MathF.Max(topLeft.Y, bottomRight.Y);

            if (!UsingBackbuffer && !IsUvOriginTopLeft)
                (top, bottom) = (Viewport.Height - bottom, Viewport.Height - top);

            // Inequalities are written so that NaN fails.
            return left >= scissor.Left && right <= scissor.Right && top >= scissor.Top && bottom <= scissor.Bottom;
        }

        private static bool isInsideMaskingInterior(in Quad quad, in MaskingInfo info, bool evaluated)
        {
            if (!evaluated)
                return true;

            if (info.Hollow || !(info.BlendRange > 0) || !(info.CornerRadius >= 0) || !(info.BorderThickness >= 0))
                return false;

            // In sh_Masking.h, a point at distance e from the nearest straight edge (and not within a corner square) has
            // dist = radius - e, while corner squares use the rounded distance. Content colour is unaffected when
            // dist <= radius + radiusCorrection - max(blendRange, borderThickness), i.e. e >= edgeInset below. The unaffected
            // region therefore contains the union of a horizontal and a vertical band, each avoiding the corner squares.
            float radius = info.CornerRadius;
            float radiusCorrection = radius <= 0 ? info.BlendRange : MathF.Max(0, info.BlendRange - radius);
            float edgeInset = MathF.Max(info.BlendRange, info.BorderThickness) - radiusCorrection + masking_interior_margin;
            float cornerInset = MathF.Max(radius + masking_interior_margin, edgeInset);

            RectangleF rect = info.MaskingRect;
            var horizontalBand = RectangleF.FromLTRB(rect.Left + edgeInset, rect.Top + cornerInset, rect.Right - edgeInset, rect.Bottom - cornerInset);
            var verticalBand = RectangleF.FromLTRB(rect.Left + cornerInset, rect.Top + edgeInset, rect.Right - cornerInset, rect.Bottom - edgeInset);

            // The masking-space image of a convex quad is contained in an axis-aligned rectangle if its corners' bounds are.
            if (!tryTransform(quad.TopLeft, info.ToMaskingSpace, out Vector2 a)
                || !tryTransform(quad.TopRight, info.ToMaskingSpace, out Vector2 b)
                || !tryTransform(quad.BottomLeft, info.ToMaskingSpace, out Vector2 c)
                || !tryTransform(quad.BottomRight, info.ToMaskingSpace, out Vector2 d))
                return false;

            var bounds = RectangleF.FromLTRB(
                MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)),
                MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
                MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)),
                MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));

            return contains(horizontalBand, bounds) || contains(verticalBand, bounds);

            // Inequalities are written so that NaN fails.
            static bool contains(in RectangleF outer, in RectangleF inner)
                => inner.Left >= outer.Left && inner.Right <= outer.Right && inner.Top >= outer.Top && inner.Bottom <= outer.Bottom;

            static bool tryTransform(Vector2 position, in Matrix3 m, out Vector2 result)
            {
                // Matches sh_Texture2D.vs, including the homogeneous divide.
                float x = m.Row0.X * position.X + m.Row1.X * position.Y + m.Row2.X;
                float y = m.Row0.Y * position.X + m.Row1.Y * position.Y + m.Row2.Y;
                float z = m.Row0.Z * position.X + m.Row1.Z * position.Y + m.Row2.Z;

                result = new Vector2(x / z, y / z);
                return z > 0;
            }
        }

        #endregion

        #region Depth & Stencil

        public void PushDepthInfo(DepthInfo depthInfo)
        {
            depthStack.Push(depthInfo);
            setDepthInfo(depthInfo);
        }

        public void PushStencilInfo(StencilInfo stencilInfo)
        {
            stencilStack.Push(stencilInfo);
            setStencilInfo(stencilInfo);
        }

        public void PopDepthInfo()
        {
            Trace.Assert(depthStack.Count > 1);

            depthStack.Pop();
            setDepthInfo(depthStack.Peek());
        }

        public void PopStencilInfo()
        {
            Trace.Assert(stencilStack.Count > 1);

            stencilStack.Pop();
            setStencilInfo(stencilStack.Peek());
        }

        private void setDepthInfo(DepthInfo depthInfo)
        {
            if (CurrentDepthInfo.Equals(depthInfo))
                return;

            FlushCurrentBatch(FlushBatchSource.SetDepthInfo);
            SetDepthInfoImplementation(depthInfo);

            CurrentDepthInfo = depthInfo;

            // Clipping in shaders does not stop depth writes, so it depends on the depth state.
            maskingStatePending = true;
        }

        private void setStencilInfo(StencilInfo stencilInfo)
        {
            if (CurrentStencilInfo.Equals(stencilInfo))
                return;

            FlushCurrentBatch(FlushBatchSource.SetStencilInfo);
            SetStencilInfoImplementation(stencilInfo);

            CurrentStencilInfo = stencilInfo;

            // Clipping in shaders does not stop stencil writes, so it depends on the stencil state.
            maskingStatePending = true;
        }

        /// <summary>
        /// Updates the graphics device with new depth parameters.
        /// </summary>
        /// <param name="depthInfo">The depth parameters to use.</param>
        protected abstract void SetDepthInfoImplementation(DepthInfo depthInfo);

        /// <summary>
        /// Updates the graphics device with new stencil parameters.
        /// </summary>
        /// <param name="stencilInfo">The stencil parameters to use.</param>
        protected abstract void SetStencilInfoImplementation(StencilInfo stencilInfo);

        #endregion

        #region Batches

        internal IVertexBatch<TexturedVertex2D> DefaultQuadBatch => quadBatches.Peek();

        internal void PushQuadBatch(IVertexBatch<TexturedVertex2D> quadBatch) => quadBatches.Push(quadBatch);

        internal void PopQuadBatch() => quadBatches.Pop();

        /// <summary>
        /// Notifies that a <see cref="IVertexBuffer"/> has begun being used.
        /// </summary>
        /// <param name="buffer">The <see cref="IVertexBuffer"/> in use.</param>
        internal void RegisterVertexBufferUse(IVertexBuffer buffer) => vertexBuffersInUse.Add(buffer);

        /// <summary>
        /// Sets the last vertex batch used for drawing.
        /// <para>
        /// This is done so that various methods that change renderer state can force-draw the batch
        /// before continuing with the state change.
        /// </para>
        /// </summary>
        /// <param name="batch">The batch.</param>
        internal void SetActiveBatch(IVertexBatch batch)
        {
            // Whether clipping can happen in the shader depends on the batch's vertex type.
            if (currentActiveBatch != batch)
                maskingStatePending = true;

            ApplyPendingMaskingState(batch);

            if (currentActiveBatch == batch)
                return;

            batchResetList.Add(batch);

            FlushCurrentBatch(FlushBatchSource.SetActiveBatch);

            currentActiveBatch = batch;
        }

        /// <summary>
        /// Flushes the currently active vertex batch.
        /// </summary>
        /// <param name="source">The source performing the flush, for profiling purposes.</param>
        protected internal void FlushCurrentBatch(FlushBatchSource? source)
        {
            if (drawCapture == null)
            {
                if (currentActiveBatch?.Draw() > 0 && source != null)
                    flush_source_statistics[(int)source].Value++;
                return;
            }

            drawCapture.FlushSource = source?.ToString();

            try
            {
                if (currentActiveBatch?.Draw() > 0 && source != null)
                    flush_source_statistics[(int)source].Value++;
            }
            finally
            {
                drawCapture.FlushSource = null;
            }
        }

        private void freeUnusedVertexBuffers()
        {
            foreach (var buf in vertexBuffersInUse)
            {
                if (buf.InUse && FrameIndex - buf.LastUseFrameIndex > RESOURCE_FREE_NO_USAGE_LENGTH)
                {
                    // Calling Free will mark InUse as false internally, which allows the cleanup below to work.
                    buf.Free();
                }
            }

            vertexBuffersInUse.RemoveAll(b => !b.InUse);
        }

        #endregion

        #region Textures

        public bool BindTexture(Texture texture, int unit, WrapMode? wrapModeS, WrapMode? wrapModeT)
        {
            ObjectDisposedException.ThrowIf(!texture.Available, texture);

            // Vertices sample the current slot when binding to unit 0.
            if (texture is TextureWhitePixel && lastBoundTextureIsAtlas[unit == 0 ? currentTextureSlot : unit])
            {
                setWrapMode(wrapModeS ?? texture.WrapModeS, wrapModeT ?? texture.WrapModeT);

                // We can use the special white space from any atlas texture.
                return true;
            }

            texture.NativeTexture.Upload();

            bool didBind = BindTexture(texture.NativeTexture, unit, wrapModeS ?? texture.WrapModeS, wrapModeT ?? texture.WrapModeT);
            lastBoundTextureIsAtlas[unit == 0 ? currentTextureSlot : unit] = texture.IsAtlasTexture && texture.NativeTexture.PixelFormat == TexturePixelFormat.R8G8B8A8Float;

            return didBind;
        }

        /// <summary>
        /// Binds a native texture. Generally used by internal components of renderer implementations.
        /// </summary>
        /// <param name="texture">The native texture to bind.</param>
        /// <param name="unit">The sampling unit in which the texture is to be bound.</param>
        /// <param name="wrapModeS">The texture's horizontal wrap mode.</param>
        /// <param name="wrapModeT">The texture's vertex wrap mode.</param>
        /// <returns>Whether the texture was successfully bound.</returns>
        public bool BindTexture(INativeTexture texture, int unit = 0, WrapMode wrapModeS = WrapMode.None, WrapMode wrapModeT = WrapMode.None)
        {
            if (unit == 0 && defaultTextureShaderActive && TextureSlotsEnabled && BindsTextureSlots)
                return bindTextureSlot(texture, wrapModeS, wrapModeT);

            if (lastActiveTextureUnit == unit && lastBoundTexture[unit] == texture)
            {
                setWrapMode(wrapModeS, wrapModeT);
                return true;
            }

            FlushCurrentBatch(FlushBatchSource.BindTexture);

            if (!SetTextureImplementation(texture, unit))
                return false;

            setWrapMode(wrapModeS, wrapModeT);
            setTextureSlotFormat(unit, texture);

            if (unit == 0)
                currentTextureSlot = 0;

            lastBoundTexture[unit] = texture;
            lastBoundTextureIsAtlas[unit] = false;
            lastActiveTextureUnit = unit;

            if (unit == 0)
                textureBoundForCurrentFrameBuffer = true;

            FrameStatistics.Increment(StatisticsCounterType.TextureBinds);
            texture.TotalBindCount++;

            return true;
        }

        /// <summary>
        /// Binds a texture for the default texture shader to whichever slot (texture unit) lets vertices already in the batch keep theirs.
        /// Only when every slot is in use by the batch does this flush.
        /// </summary>
        private bool bindTextureSlot(INativeTexture texture, WrapMode wrapModeS, WrapMode wrapModeT)
        {
            int slot = Array.IndexOf(lastBoundTexture, texture, 0, TextureSlotCount);

            if (slot < 0)
            {
                slot = findReplaceableTextureSlot();

                if (slot < 0)
                {
                    FlushCurrentBatch(FlushBatchSource.BindTexture);
                    slot = findReplaceableTextureSlot();
                }

                if (!SetTextureImplementation(texture, slot))
                    return false;

                setTextureSlotFormat(slot, texture);
                lastBoundTexture[slot] = texture;
                lastBoundTextureIsAtlas[slot] = false;
                lastActiveTextureUnit = slot;

                FrameStatistics.Increment(StatisticsCounterType.TextureBinds);
                texture.TotalBindCount++;
            }
            else if (lastActiveTextureUnit != slot)
            {
                // GL texture uploads bind through here, then write to the active unit's texture. Reattaching the same texture needs no flush.
                if (!SetTextureImplementation(texture, slot))
                    return false;

                lastActiveTextureUnit = slot;
            }

            currentTextureSlot = slot;
            textureBoundForCurrentFrameBuffer = true;

            // Wrap modes are uniforms for the whole batch, so a change still flushes.
            setWrapMode(wrapModeS, wrapModeT);
            return true;
        }

        /// <summary>
        /// A slot no vertex in the current batch samples, preferring empty ones, or -1 if all are in use.
        /// </summary>
        private int findReplaceableTextureSlot()
        {
            for (int i = 0; i < TextureSlotCount; i++)
            {
                if (lastBoundTexture[i] == null && (textureSlotsUsedInBatch & (1 << i)) == 0)
                    return i;
            }

            for (int i = 1; i <= TextureSlotCount; i++)
            {
                int slot = (lastReplacedTextureSlot + i) % TextureSlotCount;

                if ((textureSlotsUsedInBatch & (1 << slot)) == 0)
                    return lastReplacedTextureSlot = slot;
            }

            return -1;
        }

        /// <summary>
        /// Attaches the white pixel's texture to slots without a texture, which shaders cannot leave unbound.
        /// </summary>
        private void fillEmptyTextureSlots()
        {
            for (int i = 0; i < TextureSlotCount; i++)
            {
                if (lastBoundTexture[i] != null)
                    continue;

                INativeTexture white = WhitePixel.NativeTexture;
                white.Upload();

                if (!SetTextureImplementation(white, i))
                    continue;

                setTextureSlotFormat(i, white);
                lastBoundTexture[i] = white;
                lastBoundTextureIsAtlas[i] = WhitePixel.IsAtlasTexture && white.PixelFormat == TexturePixelFormat.R8G8B8A8Float;
                lastActiveTextureUnit = i;
            }
        }

        private void setTextureSlotFormat(int unit, INativeTexture texture)
        {
            if (unit >= TextureSlotCount)
                return;

            int bit = 1 << unit;
            int slots = texture.PixelFormat == TexturePixelFormat.R8Float ? singleChannelSlots | bit : singleChannelSlots & ~bit;
            int nearest = UsesNearestFiltering(texture) ? nearestSlots | bit : nearestSlots & ~bit;

            if (slots == singleChannelSlots && nearest == nearestSlots)
                return;

            singleChannelSlots = slots;
            nearestSlots = nearest;
            globalUniformsChanged = true;
        }

        private void setWrapMode(WrapMode wrapModeS, WrapMode wrapModeT)
        {
            if (wrapModeS != CurrentWrapModeS)
            {
                FlushCurrentBatch(FlushBatchSource.BindTexture);

                CurrentWrapModeS = wrapModeS;
                globalUniformsChanged = true;
            }

            if (wrapModeT != CurrentWrapModeT)
            {
                FlushCurrentBatch(FlushBatchSource.BindTexture);

                CurrentWrapModeT = wrapModeT;
                globalUniformsChanged = true;
            }
        }

        /// <summary>
        /// Unbinds any bound texture.
        /// </summary>
        /// <param name="unit">The sampling unit in which the texture is to be unbound.</param>
        public void UnbindTexture(int unit = 0)
        {
            if (lastBoundTexture[unit] == null)
                return;

            FlushCurrentBatch(FlushBatchSource.UnbindTexture);
            SetTextureImplementation(null, unit);

            lastBoundTexture[unit] = null;
            lastBoundTextureIsAtlas[unit] = false;
        }

        /// <summary>
        /// Enqueues a texture to be uploaded in the next frame.
        /// </summary>
        /// <param name="texture">The texture to be uploaded.</param>
        internal void EnqueueTextureUpload(INativeTexture texture)
        {
            if (!IsInitialised)
                return;

            lock (textureUploadQueue)
            {
                if (queuedTexturesForUpload.Add(texture))
                    textureUploadQueue.Enqueue(texture);
            }
        }

        private bool tryDequeueTextureUpload([NotNullWhen(true)] out INativeTexture? texture)
        {
            // Upload must run after this method returns: SetData schedules while holding the texture's own queue lock.
            lock (textureUploadQueue)
            {
                if (!textureUploadQueue.TryDequeue(out texture))
                    return false;

                // Release membership before uploading so new data can schedule a subsequent entry.
                queuedTexturesForUpload.Remove(texture);
                return true;
            }
        }

        /// <summary>
        /// Informs the graphics device to use the given texture for drawing.
        /// </summary>
        /// <param name="texture">The texture, or null to use default texture.</param>
        /// <param name="unit">The sampling unit in which the texture is to be bound.</param>
        /// <returns>Whether the texture was set successfully.</returns>
        protected abstract bool SetTextureImplementation(INativeTexture? texture, int unit);

        #endregion

        #region Framebuffers

        public void BindFrameBuffer(IFrameBuffer frameBuffer)
        {
            frameBufferStack.Push(frameBuffer);
            setFrameBuffer(frameBuffer);
        }

        public void UnbindFrameBuffer(IFrameBuffer? frameBuffer)
        {
            if (FrameBuffer != frameBuffer)
                return;

            frameBufferStack.Pop();
            setFrameBuffer(frameBufferStack.TryPeek(out var lastFramebuffer) ? lastFramebuffer : null);
        }

        private void setFrameBuffer(IFrameBuffer? frameBuffer, bool force = false)
        {
            if (frameBuffer == FrameBuffer && !force)
                return;

            FlushCurrentBatch(FlushBatchSource.SetFrameBuffer);
            SetFrameBufferImplementation(frameBuffer);

            FrameBuffer = frameBuffer;

            UsingBackbuffer = frameBuffer == null;
            globalUniformsChanged = true;
            invalidateAppliedScissor();

            // The previously bound texture may be this framebuffer's own attachment. Don't leave it attached for untextured draws.
            textureBoundForCurrentFrameBuffer = false;

            // Nor in any unit the default texture shader samples: treat them as empty, so they are rebound or filled before its next draw.
            lastBoundTexture.AsSpan(0, TextureSlotCount).Clear();
            lastBoundTextureIsAtlas.AsSpan(0, TextureSlotCount).Clear();
        }

        /// <summary>
        /// Whether white pixel quads may use the untextured path. Disabling it restores binding and sampling, for comparison.
        /// </summary>
        internal bool UntexturedWhitePixel { get; set; } = true;

        /// <summary>
        /// Whether a texture was bound to unit 0 since the last framebuffer change.
        /// </summary>
        private bool textureBoundForCurrentFrameBuffer;

        /// <summary>
        /// Whether <paramref name="texture"/> can be drawn without binding it, using the default texture shader's untextured mode.
        /// <para>
        /// The white pixel samples as opaque white everywhere within its rectangle. Quads marking <c>TextureRect</c> with
        /// <see cref="UNTEXTURED_RECT"/> skip wrapping and sampling in <c>sh_Texture.fs</c>, so whichever texture is bound stays
        /// bound. This avoids flushing when boxes alternate with non-atlas textures, as well as a texture fetch per fragment.
        /// </para>
        /// </summary>
        internal bool CanDrawUntextured(Texture texture)
            => UntexturedWhitePixel
               && texture is TextureWhitePixel
               && texture.WrapModeS == WrapMode.None && texture.WrapModeT == WrapMode.None
               && textureBoundForCurrentFrameBuffer && lastBoundTexture[0] != null
               && isDefaultTextureShaderBound;

        /// <summary>
        /// Texture-space rectangle marking an untextured quad. Must match the threshold in <c>sh_Texture.fs</c>.
        /// </summary>
        internal static readonly RectangleF UNTEXTURED_RECT = new RectangleF(-2, -2, 1, 1);

        /// <summary>
        /// Informs the graphics device to use the given framebuffer for drawing.
        /// </summary>
        /// <param name="frameBuffer">The framebuffer to use, or null to use the backbuffer (i.e. main framebuffer).</param>
        protected abstract void SetFrameBufferImplementation(IFrameBuffer? frameBuffer);

        /// <summary>
        /// Deletes a frame buffer.
        /// </summary>
        /// <param name="frameBuffer">The frame buffer to delete.</param>
        public void DeleteFrameBuffer(IFrameBuffer frameBuffer)
        {
            while (FrameBuffer == frameBuffer)
                UnbindFrameBuffer(frameBuffer);

            DeleteFrameBufferImplementation(frameBuffer);
        }

        protected abstract void DeleteFrameBufferImplementation(IFrameBuffer frameBuffer);

        #endregion

        public void DrawVertices(PrimitiveTopology topology, int vertexStart, int verticesCount)
        {
            ApplyPendingMaskingState();
            DrawBatchVertices(topology, vertexStart, verticesCount);
        }

        // Buffered vertices retain the clipping state from submission, even if the logical
        // masking stack has since changed. Do not apply pending state while flushing them.
        internal void DrawBatchVertices(PrimitiveTopology topology, int vertexStart, int verticesCount)
        {
            if (Shader == null)
                throw new InvalidOperationException("No shader bound.");

            // The default texture shader declares every slot, so each needs a texture attached, even with slots disabled.
            if (defaultTextureShaderActive)
                fillEmptyTextureSlots();

            if (globalUniformsChanged)
            {
                globalUniformBuffer!.Data = new GlobalUniformData
                {
                    BackbufferDraw = UsingBackbuffer,
                    IsDepthRangeZeroToOne = IsDepthRangeZeroToOne,
                    IsClipSpaceYInverted = IsClipSpaceYInverted,
                    IsUvOriginTopLeft = IsUvOriginTopLeft,
                    ProjMatrix = ProjectionMatrix,
                    ToMaskingSpace = appliedMaskingInfo.ToMaskingSpace,
                    IsMasking = appliedMaskingActive,
                    CornerRadius = appliedMaskingInfo.CornerRadius,
                    CornerExponent = appliedMaskingInfo.CornerExponent,
                    MaskingRect = new Vector4(
                        appliedMaskingInfo.MaskingRect.Left,
                        appliedMaskingInfo.MaskingRect.Top,
                        appliedMaskingInfo.MaskingRect.Right,
                        appliedMaskingInfo.MaskingRect.Bottom),
                    BorderThickness = appliedMaskingInfo.BorderThickness / appliedMaskingInfo.BlendRange,
                    BorderColour = appliedMaskingInfo.BorderThickness > 0
                        ? new Matrix4(
                            // TopLeft
                            appliedMaskingInfo.BorderColour.TopLeft.SRGB.R,
                            appliedMaskingInfo.BorderColour.TopLeft.SRGB.G,
                            appliedMaskingInfo.BorderColour.TopLeft.SRGB.B,
                            appliedMaskingInfo.BorderColour.TopLeft.SRGB.A,
                            // BottomLeft
                            appliedMaskingInfo.BorderColour.BottomLeft.SRGB.R,
                            appliedMaskingInfo.BorderColour.BottomLeft.SRGB.G,
                            appliedMaskingInfo.BorderColour.BottomLeft.SRGB.B,
                            appliedMaskingInfo.BorderColour.BottomLeft.SRGB.A,
                            // TopRight
                            appliedMaskingInfo.BorderColour.TopRight.SRGB.R,
                            appliedMaskingInfo.BorderColour.TopRight.SRGB.G,
                            appliedMaskingInfo.BorderColour.TopRight.SRGB.B,
                            appliedMaskingInfo.BorderColour.TopRight.SRGB.A,
                            // BottomRight
                            appliedMaskingInfo.BorderColour.BottomRight.SRGB.R,
                            appliedMaskingInfo.BorderColour.BottomRight.SRGB.G,
                            appliedMaskingInfo.BorderColour.BottomRight.SRGB.B,
                            appliedMaskingInfo.BorderColour.BottomRight.SRGB.A)
                        : globalUniformBuffer.Data.BorderColour,
                    MaskingBlendRange = appliedMaskingInfo.BlendRange,
                    AlphaExponent = appliedMaskingInfo.AlphaExponent,
                    EdgeOffset = appliedMaskingInfo.EdgeOffset,
                    DiscardInner = appliedMaskingInfo.Hollow,
                    InnerCornerRadius = appliedMaskingInfo.Hollow
                        ? appliedMaskingInfo.HollowCornerRadius
                        : globalUniformBuffer.Data.InnerCornerRadius,
                    WrapModeS = (int)CurrentWrapModeS,
                    WrapModeT = (int)CurrentWrapModeT,
                    MaskingIndex = Math.Max(appliedMaskingEntry, 0),
                    SingleChannelSlots = singleChannelSlots,
                    NearestSlots = nearestSlots,
                };

                globalUniformsChanged = false;
            }

            Shader.BindUniformBlock("g_GlobalUniforms", globalUniformBuffer!);

            // Bound without flushing: a flush is in progress, and the buffer only changes after a flush on overflow.
            if (maskingBuffers.Count > 0 && (boundMaskingBuffer != maskingBuffers[maskingBufferIndex] || !boundUniformBuffers.ContainsKey(masking_buffer_name)))
            {
                boundMaskingBuffer = maskingBuffers[maskingBufferIndex];
                boundUniformBuffers[masking_buffer_name] = boundMaskingBuffer;
                SetUniformBufferImplementation(masking_buffer_name, boundMaskingBuffer);
            }

            if (defaultTextureShaderActive)
            {
                BeginTextureSlotDraw();
                DrawVerticesImplementation(topology, vertexStart, verticesCount);
                EndTextureSlotDraw();
            }
            else
                DrawVerticesImplementation(topology, vertexStart, verticesCount);

            // A batch drawing because it is full continues with the primitive being added, which was stamped with the current slot.
            textureSlotsUsedInBatch = 1 << currentTextureSlot;

            drawCapture?.Record(topology, vertexStart, verticesCount, Shader, lastBoundTexture[0], UsingBackbuffer, currentActiveBatch);
            FrameStatistics.Increment(StatisticsCounterType.DrawCalls);
            FrameStatistics.Add(StatisticsCounterType.VerticesDraw, verticesCount);
        }

        public abstract void DrawVerticesImplementation(PrimitiveTopology topology, int vertexStart, int verticesCount);

        #region Shaders

        public void BindShader(IShader shader)
        {
            bool alreadyBound = shaderStack.Count > 0 && shaderStack.Peek() == shader;

            shaderStack.Push(shader);

            if (!alreadyBound)
                setShader(shader);
        }

        public void UnbindShader(IShader shader)
        {
            if (shaderStack.Peek() != shader)
                throw new InvalidOperationException("Attempting to unbind shader while isn't the latest bound shader.");

            shaderStack.Pop();
            setShader(shaderStack.TryPeek(out var lastShader) ? lastShader : null);
        }

        private void setShader(IShader? shader)
        {
            ThreadSafety.EnsureDrawThread();

            if (shader == Shader)
                return;

            if (shader != null)
            {
                FrameStatistics.Increment(StatisticsCounterType.ShaderBinds);

                FlushCurrentBatch(FlushBatchSource.SetShader);
                SetShaderImplementation(shader);

                // Whether clipping can happen in the shader depends on the shader.
                maskingStatePending = true;

                // importantly, when a shader is unbound, it remains bound in the implementation.
                // to save VBO flushing overhead, keep reference of the last shader.
                Shader = shader;

                // Other shaders sample unit 0 only.
                defaultTextureShaderActive = defaultTextureShaders.TryGetValue(shader, out _);
                if (!defaultTextureShaderActive)
                    currentTextureSlot = 0;
            }
        }

        internal void SetUniform<T>(IUniformWithValue<T> uniform)
            where T : unmanaged, IEquatable<T>
        {
            if (uniform.Owner == Shader)
                FlushCurrentBatch(FlushBatchSource.SetUniform);

            SetUniformImplementation(uniform);
        }

        /// <summary>
        /// Informs the graphics device to use the given shader for drawing.
        /// </summary>
        /// <param name="shader">The shader to use.</param>
        protected abstract void SetShaderImplementation(IShader shader);

        /// <summary>
        /// Informs the graphics device to update the value of the given uniform.
        /// </summary>
        /// <param name="uniform">The uniform to update.</param>
        protected abstract void SetUniformImplementation<T>(IUniformWithValue<T> uniform) where T : unmanaged, IEquatable<T>;

        public void BindUniformBuffer(string blockName, IUniformBuffer buffer)
        {
            if (boundUniformBuffers.TryGetValue(blockName, out IUniformBuffer? current) && current == buffer)
                return;

            FlushCurrentBatch(FlushBatchSource.BindBuffer);
            SetUniformBufferImplementation(blockName, buffer);

            boundUniformBuffers[blockName] = buffer;
        }

        protected abstract void SetUniformBufferImplementation(string blockName, IUniformBuffer buffer);

        #endregion

        #region Factory

        public abstract IFrameBuffer CreateFrameBuffer(TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, RenderBufferFormat[]? renderBufferFormats = null, TextureFilteringMode filteringMode = TextureFilteringMode.Linear);

        /// <inheritdoc cref="IRenderer.CreateShaderPart"/>
        protected abstract IShaderPart CreateShaderPart(IShaderStore store, string name, byte[]? rawData, ShaderPartType partType);

        /// <inheritdoc cref="IRenderer.CreateShader"/>
        protected abstract IShader CreateShader(string name, IShaderPart[] parts, ShaderCompilationStore compilationStore);

        private IShader? mipmapShader;
        private IShader? blitShader;

        internal IShader GetBlitShader()
        {
            if (blitShader != null)
                return blitShader;

            var store = new PassthroughShaderStore(
                new NamespacedResourceStore<byte[]>(
                    new NamespacedResourceStore<byte[]>(
                        new DllResourceStore(typeof(Game).Assembly),
                        @"Resources"),
                    "Shaders"));

            blitShader = CreateShader("Texture2D", new[]
            {
                CreateShaderPart(store, "Texture2D.vs", store.GetRawData("sh_Texture2D.vs"), ShaderPartType.Vertex),
                CreateShaderPart(store, "Texture.fs", store.GetRawData("sh_Texture.fs"), ShaderPartType.Fragment),
            }, shaderCompilationStore);

            return blitShader;
        }

        internal IShader GetMipmapShader()
        {
            if (mipmapShader != null)
                return mipmapShader;

            var store = new PassthroughShaderStore(
                new NamespacedResourceStore<byte[]>(
                    new NamespacedResourceStore<byte[]>(
                        new DllResourceStore(typeof(Game).Assembly),
                        @"Resources"),
                    "Shaders"));

            mipmapShader = CreateShader("mipmap", new[]
            {
                CreateShaderPart(store, "mipmap.vs", store.GetRawData("sh_mipmap.vs"), ShaderPartType.Vertex),
                CreateShaderPart(store, "mipmap.fs", store.GetRawData("sh_mipmap.fs"), ShaderPartType.Fragment),
            }, shaderCompilationStore);

            return mipmapShader;
        }

        /// <inheritdoc cref="IRenderer.CreateLinearBatch{TVertex}"/>
        protected abstract IVertexBatch<TVertex> CreateLinearBatch<TVertex>(int size, int maxBuffers, PrimitiveTopology topology) where TVertex : unmanaged, IEquatable<TVertex>, IVertex;

        /// <inheritdoc cref="IRenderer.CreateQuadBatch{TVertex}"/>
        protected abstract IVertexBatch<TVertex> CreateQuadBatch<TVertex>(int size, int maxBuffers) where TVertex : unmanaged, IEquatable<TVertex>, IVertex;

        /// <inheritdoc cref="IRenderer.CreateUniformBuffer{TData}"/>
        protected abstract IUniformBuffer<TData> CreateUniformBuffer<TData>() where TData : unmanaged, IEquatable<TData>;

        /// <inheritdoc cref="IRenderer.CreateShaderStorageBufferObject{TData}"/>
        protected abstract IShaderStorageBufferObject<TData> CreateShaderStorageBufferObject<TData>(int uboSize, int ssboSize) where TData : unmanaged, IEquatable<TData>;

        /// <summary>
        /// Creates a new <see cref="INativeTexture"/>.
        /// </summary>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        /// <param name="manualMipmaps">Whether manual mipmaps will be uploaded to the texture. If false, the texture will compute mipmaps automatically.</param>
        /// <param name="filteringMode">The filtering mode.</param>
        /// <param name="initialisationColour">The colour to initialise texture levels with (in the case of sub region initial uploads). If null, no initialisation is provided out-of-the-box.</param>
        /// <param name="textureFormat">The pixel format of the texture.</param>
        /// <returns>The <see cref="INativeTexture"/>.</returns>
        protected abstract INativeTexture CreateNativeTexture(int width, int height, bool manualMipmaps = false, TextureFilteringMode filteringMode = TextureFilteringMode.Linear,
                                                              Color4? initialisationColour = null, TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float);

        /// <summary>
        /// Creates a new <see cref="INativeTexture"/> for video sprites.
        /// </summary>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        /// <returns>The video <see cref="INativeTexture"/>.</returns>
        protected abstract INativeTexture CreateNativeVideoTexture(int width, int height);

        public Texture CreateTexture(int width, int height, bool manualMipmaps, TextureFilteringMode filteringMode, WrapMode wrapModeS, WrapMode wrapModeT,
                                     Color4? initialisationColour)
            => CreateTexture(width, height, manualMipmaps, filteringMode, wrapModeS, wrapModeT, initialisationColour, TexturePixelFormat.R8G8B8A8Float);

        public Texture CreateTexture(int width, int height, bool manualMipmaps, TextureFilteringMode filteringMode, WrapMode wrapModeS, WrapMode wrapModeT, Color4? initialisationColour,
                                     TexturePixelFormat textureFormat)
            => CreateTexture(CreateNativeTexture(width, height, manualMipmaps, filteringMode, initialisationColour, textureFormat), wrapModeS, wrapModeT);

        public Texture CreateVideoTexture(int width, int height) => CreateTexture(CreateNativeVideoTexture(width, height));

        /// <summary>
        /// Creates a new <see cref="Texture"/> based off an <see cref="INativeTexture"/>.
        /// </summary>
        /// <param name="nativeTexture">The <see cref="INativeTexture"/> to create the texture with.</param>
        /// <param name="wrapModeS">The horizontal wrap mode of the texture.</param>
        /// <param name="wrapModeT">The vertical wrap mode of the texture.</param>
        /// <returns>The <see cref="Texture"/>.</returns>
        internal Texture CreateTexture(INativeTexture nativeTexture, WrapMode wrapModeS = WrapMode.None, WrapMode wrapModeT = WrapMode.None)
            => registerTexture(new Texture(nativeTexture, wrapModeS, wrapModeT));

        private Texture registerTexture(Texture texture)
        {
            allTextures.Add(texture);
            TextureCreated?.Invoke(texture);
            return texture;
        }

        #endregion

        #region IRenderer explicit implementation

        bool IRenderer.VerticalSync
        {
            get => VerticalSync;
            set => VerticalSync = value;
        }

        bool IRenderer.AllowTearing
        {
            get => AllowTearing;
            set => AllowTearing = value;
        }

        Storage? IRenderer.CacheStorage
        {
            set => CacheStorage = value;
        }

        IVertexBatch<TexturedVertex2D> IRenderer.DefaultQuadBatch => DefaultQuadBatch;
        void IRenderer.BeginFrame(Vector2 windowSize) => BeginFrame(windowSize);
        void IRenderer.FinishFrame() => FinishFrame();
        void IRenderer.FlushCurrentBatch(FlushBatchSource? source) => FlushCurrentBatch(source);
        void IRenderer.SwapBuffers() => SwapBuffers();
        void IRenderer.WaitUntilIdle() => WaitUntilIdle();
        void IRenderer.WaitUntilNextFrameReady() => WaitUntilNextFrameReady();
        void IRenderer.MakeCurrent() => MakeCurrent();
        void IRenderer.ClearCurrent() => ClearCurrent();
        void IRenderer.SetUniform<T>(IUniformWithValue<T> uniform) => SetUniform(uniform);
        void IRenderer.PushQuadBatch(IVertexBatch<TexturedVertex2D> quadBatch) => PushQuadBatch(quadBatch);
        void IRenderer.PopQuadBatch() => PopQuadBatch();
        Image<Rgba32> IRenderer.TakeScreenshot() => TakeScreenshot();
        Image<Rgba32>? IRenderer.ExtractFrameBufferData(IFrameBuffer frameBuffer) => ExtractFrameBufferData(frameBuffer);
        IShaderPart IRenderer.CreateShaderPart(IShaderStore store, string name, byte[]? rawData, ShaderPartType partType) => CreateShaderPart(store, name, rawData, partType);
        IShader IRenderer.CreateShader(string name, IShaderPart[] parts)
        {
            IShader shader = CreateShader(name, parts, shaderCompilationStore);

            // The default sprite/text shader only depends on clipping state via getRoundedColor().
            if (name == $"{VertexShaderDescriptor.TEXTURE_2}/{FragmentShaderDescriptor.TEXTURE}")
                defaultTextureShaders.AddOrUpdate(shader, null);

            return shader;
        }

        IVertexBatch<TVertex> IRenderer.CreateLinearBatch<TVertex>(int size, int maxBuffers, PrimitiveTopology topology)
        {
            if (size <= 0)
                throw new ArgumentException("Linear batch size must be > 0.", nameof(size));

            if (size > IRenderer.MAX_VERTICES)
                throw new ArgumentException($"Linear batch may not have more than {IRenderer.MAX_VERTICES} vertices.", nameof(size));

            if (maxBuffers <= 0)
                throw new ArgumentException("Maximum number of buffers must be > 0.", nameof(maxBuffers));

            return CreateLinearBatch<TVertex>(size, maxBuffers, topology);
        }

        IVertexBatch<TVertex> IRenderer.CreateQuadBatch<TVertex>(int size, int maxBuffers)
        {
            if (size <= 0)
                throw new ArgumentException("Quad batch size must be > 0.", nameof(size));

            if (size > IRenderer.MAX_QUADS)
                throw new ArgumentException($"Quad batch may not have more than {IRenderer.MAX_QUADS} quads.", nameof(size));

            if (maxBuffers <= 0)
                throw new ArgumentException("Maximum number of buffers must be > 0.", nameof(maxBuffers));

            return CreateQuadBatch<TVertex>(size, maxBuffers);
        }

        private readonly HashSet<Type> validUboTypes = new HashSet<Type>();

        IUniformBuffer<TData> IRenderer.CreateUniformBuffer<TData>()
        {
            validateUniformLayout<TData>();
            return CreateUniformBuffer<TData>();
        }

        IShaderStorageBufferObject<TData> IRenderer.CreateShaderStorageBufferObject<TData>(int uboSize, int ssboSize)
        {
            validateUniformLayout<TData>();
            return CreateShaderStorageBufferObject<TData>(uboSize, ssboSize);
        }

        private void validateUniformLayout<TData>()
        {
            if (validUboTypes.Contains(typeof(TData)))
                return;

            if (typeof(TData).StructLayoutAttribute?.Pack != 1)
                throw new ArgumentException($"{typeof(TData).ReadableName()} requires a packing size of 1.");

            int offset = 0;

            foreach (var field in typeof(TData).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                checkValidType(field);

                if (field.FieldType == typeof(UniformMatrix3)
                    || field.FieldType == typeof(UniformMatrix4)
                    || field.FieldType == typeof(UniformVector3)
                    || field.FieldType == typeof(UniformVector4))
                {
                    checkAlignment(field, offset, 16);
                }

                if (field.FieldType == typeof(UniformVector2))
                    checkAlignment(field, offset, 8);

                offset += Marshal.SizeOf(field.FieldType);
            }

            Type? finalPadding = suggestPadding(offset, 16);
            if (finalPadding != null)
                throw new ArgumentException($"{typeof(TData).ReadableName()} alignment requires a {finalPadding} to be added at the end.");

            validUboTypes.Add(typeof(TData));
            return;

            static void checkValidType(FieldInfo field)
            {
                if (field.FieldType == typeof(UniformBool)
                    || field.FieldType == typeof(UniformFloat)
                    || field.FieldType == typeof(UniformInt)
                    || field.FieldType == typeof(UniformMatrix3)
                    || field.FieldType == typeof(UniformMatrix4)
                    || field.FieldType == typeof(UniformPadding4)
                    || field.FieldType == typeof(UniformPadding8)
                    || field.FieldType == typeof(UniformPadding12)
                    || field.FieldType == typeof(UniformVector2)
                    || field.FieldType == typeof(UniformVector3)
                    || field.FieldType == typeof(UniformVector4))
                {
                    return;
                }

                throw new ArgumentException($"{typeof(TData).ReadableName()} has an unsupported type of {field.FieldType} for field \"{field.Name}\".");
            }

            static void checkAlignment(FieldInfo field, int offset, int expectedAlignment)
            {
                Type? suggestedPadding = suggestPadding(offset, expectedAlignment);
                if (suggestedPadding != null)
                    throw new ArgumentException($"{typeof(TData).ReadableName()} alignment requires a {suggestedPadding} to be inserted before \"{field.Name}\".");
            }

            static Type? suggestPadding(int offset, int expectedAlignment)
            {
                int currentAlignment = offset % expectedAlignment;
                int paddingRequired = expectedAlignment - currentAlignment;

                if (currentAlignment == 0)
                    return null;

                return paddingRequired switch
                {
                    4 => typeof(UniformPadding4),
                    8 => typeof(UniformPadding8),
                    12 => typeof(UniformPadding12),
                    _ => null
                };
            }
        }

        #endregion

        #region TextureVisualiser support

        /// <summary>
        /// An event which is invoked every time a <see cref="Texture"/> is created.
        /// </summary>
        internal event Action<Texture>? TextureCreated;

        event Action<Texture>? IRenderer.TextureCreated
        {
            add => TextureCreated += value;
            remove => TextureCreated -= value;
        }

        Texture[] IRenderer.GetAllTextures() => allTextures.ToArray();

        #endregion

        private class PassthroughShaderStore : IShaderStore
        {
            private readonly IResourceStore<byte[]> store;

            public PassthroughShaderStore(IResourceStore<byte[]> store)
            {
                this.store = store;
            }

            public byte[]? GetRawData(string fileName) => store.Get(fileName);
        }
    }
}
