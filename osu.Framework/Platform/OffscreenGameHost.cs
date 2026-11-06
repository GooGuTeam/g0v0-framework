// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using osu.Framework.Configuration;
using osu.Framework.Development;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Imaging;
using osu.Framework.Input.Handlers;
using osu.Framework.Logging;
using osu.Framework.Statistics;
using osu.Framework.Timing;
using osuTK;

namespace osu.Framework.Platform
{
    /// <summary>
    /// A game host that draws on a real GPU into an offscreen framebuffer instead of a window.
    /// </summary>
    /// <remarks>
    /// Frames run one after another on a single thread, as fast as possible. Scene time advances by <see cref="FRAME_STEP"/> each frame,
    /// so a scene looks the same at a given frame however long the frames take. Each frame waits for the GPU before the next starts,
    /// so a frame's time includes its GPU work and frames never queue up.
    /// The device is Direct3D 11 on Windows, Metal on macOS and Vulkan on Linux, or Vulkan when the OfflineVulkanRenderer framework setting is enabled.
    /// </remarks>
    public class OffscreenGameHost : DesktopGameHost
    {
        /// <summary>
        /// The scene time between frames, in milliseconds.
        /// </summary>
        public const double FRAME_STEP = 1000.0 / 60;

        /// <summary>
        /// The draw thread counters recorded for each frame.
        /// </summary>
        private static readonly StatisticsCounterType[] recorded_counters =
        {
            StatisticsCounterType.DrawCalls,
            StatisticsCounterType.VerticesDraw,
            StatisticsCounterType.TextureBinds,
            StatisticsCounterType.ShaderBinds,
            StatisticsCounterType.FBORedraw,
            StatisticsCounterType.VBufBinds,
            StatisticsCounterType.VerticesUpl,
            StatisticsCounterType.UniformUpl,
        };

        private readonly int width;
        private readonly int height;
        private readonly string storagePath;

        private readonly ManualClock sceneTime = new ManualClock { IsRunning = true };
        private readonly FramedClock sceneClock;

        private VeldridRenderer? renderer;

        private OffscreenFrameRecording? recording;
        private OffscreenFrameRecording? completedRecording;
        private int recordedFrames;
        private int[] collectionsAtStart = Array.Empty<int>();

        private double updateTime;
        private long allocatedAtUpdate;
        private double gpuTime;

        /// <param name="gameName">The name of the game, which names its storage directory.</param>
        /// <param name="width">The width of the offscreen framebuffer.</param>
        /// <param name="height">The height of the offscreen framebuffer.</param>
        /// <param name="storagePath">The directory containing the game's storage directory.</param>
        /// <param name="options">Host options.</param>
        public OffscreenGameHost(string gameName, int width, int height, string storagePath, HostOptions? options = null)
            : base(gameName, options)
        {
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentException("Offscreen dimensions must be positive even numbers.");

            this.width = width;
            this.height = height;
            this.storagePath = storagePath;

            sceneClock = new FramedClock(sceneTime);
        }

        /// <summary>
        /// The graphics backend and adapter, once the renderer is initialised.
        /// </summary>
        public string DeviceDescription => renderer == null ? string.Empty : $@"{renderer.Device.BackendType} / {renderer.Device.DeviceName}";

        public override IEnumerable<string> UserStoragePaths => storagePath.Yield();

        protected override bool RequireWindowExists => false;

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface) => null!;

        protected override Clipboard CreateClipboard() => new HeadlessClipboard();

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers() => Array.Empty<InputHandler>();

        protected override IFrameBasedClock SceneGraphClock => sceneClock;

        protected override Vector2 BackbufferSize => new Vector2(width, height);

        public override bool OpenFileExternally(string filename)
        {
            Logger.Log($"Application has requested file \"{filename}\" to be opened.");
            return true;
        }

        public override bool PresentFileExternally(string filename)
        {
            Logger.Log($"Application has requested file \"{filename}\" to be shown.");
            return true;
        }

        public override void OpenUrlExternally(string url) => Logger.Log($"Application has requested URL \"{url}\" to be opened.");

        protected override void SetupConfig(IDictionary<FrameworkSetting, object> defaultOverrides)
        {
            defaultOverrides[FrameworkSetting.AudioDevice] = "No sound";

            base.SetupConfig(defaultOverrides);

            // The root takes its size from this setting when there is no window.
            Config.SetValue(FrameworkSetting.WindowedSize, new Size(width, height));
            Config.SetValue(FrameworkSetting.ExecutionMode, ExecutionMode.SingleThread);
        }

        protected override void SetupForRun()
        {
            base.SetupForRun();

            MaximumUpdateHz = MaximumDrawHz = MaximumInactiveHz = 0;
        }

        protected override void ChooseAndSetupRenderer()
        {
            bool vulkan = FrameworkFeatureFlags.OfflineVulkanRenderer;
            var surface = vulkan || OperatingSystem.IsLinux() ? GraphicsSurfaceType.Vulkan
                : OperatingSystem.IsMacOS() ? GraphicsSurfaceType.Metal
                : GraphicsSurfaceType.Direct3D11;

            renderer = new VeldridRenderer();

            // Without a window this applies the renderer settings and stops before initialisation.
            SetupRendererAndWindow(renderer, surface);

            // The draw pass tests depth against the backbuffer, so it needs a depth attachment like a swapchain's.
            renderer.InitialiseOffline((uint)width, (uint)height, depth: true);
            Logger.Log($"🖼️ Offscreen renderer initialised: {DeviceDescription}, {width}x{height}");
        }

        /// <summary>
        /// Records the timings of the next <paramref name="frames"/> frames. Call from the update thread.
        /// </summary>
        public void StartRecording(int frames)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

            recording = new OffscreenFrameRecording(frames, recorded_counters);
            completedRecording = null;
            recordedFrames = 0;
            collectionsAtStart = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        }

        /// <summary>
        /// Takes the recording started by <see cref="StartRecording"/>, once all its frames are recorded.
        /// </summary>
        /// <returns>The recording, or <c>null</c> while frames remain.</returns>
        public OffscreenFrameRecording? TakeRecording()
        {
            var result = completedRecording;
            completedRecording = null;
            return result;
        }

        /// <summary>
        /// Reads the last drawn frame. The image must be disposed by the caller.
        /// </summary>
        public Task<Image<Rgba32>> CaptureAsync()
        {
            var source = new TaskCompletionSource<Image<Rgba32>>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The scheduler runs before the next frame is drawn.
            DrawThread.Scheduler.Add(() =>
            {
                try
                {
                    source.SetResult(renderer!.TakeScreenshot());
                }
                catch (Exception e)
                {
                    source.SetException(e);
                }
            });

            return source.Task;
        }

        /// <summary>
        /// Whether scene time stands still, so that frames keep drawing the same scene.
        /// Benchmarks hold a scene like this while the runtime and clocks settle, without changing what is drawn when.
        /// </summary>
        public bool FreezeTime { get; set; }

        protected override void UpdateFrame()
        {
            if (!FreezeTime)
                sceneTime.CurrentTime += FRAME_STEP;

            sceneClock.ProcessFrame();

            long start = Stopwatch.GetTimestamp();
            allocatedAtUpdate = GC.GetTotalAllocatedBytes();

            base.UpdateFrame();

            updateTime = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        protected override void DrawFrame()
        {
            long start = Stopwatch.GetTimestamp();
            gpuTime = 0;

            base.DrawFrame();

            double drawTime = Stopwatch.GetElapsedTime(start).TotalMilliseconds - gpuTime;

            if (recording == null)
                return;

            recording.Add(recordedFrames, updateTime, drawTime, gpuTime, GC.GetTotalAllocatedBytes() - allocatedAtUpdate);

            for (int i = 0; i < recorded_counters.Length; i++)
                recording.Counters[i][recordedFrames] = FrameStatistics.COUNTERS[(int)recorded_counters[i]];

            if (++recordedFrames < recording.Frames)
                return;

            for (int i = 0; i < collectionsAtStart.Length; i++)
                recording.Collections[i] = GC.CollectionCount(i) - collectionsAtStart[i];

            completedRecording = recording;
            recording = null;
        }

        protected override void Swap()
        {
            // Nothing is presented. Waiting here puts the frame's GPU work into its time.
            long start = Stopwatch.GetTimestamp();
            renderer!.WaitForGpu();
            gpuTime = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (renderer?.IsInitialised != true)
                return;

            // The game threads have stopped, so this thread releases the device in the draw thread's place.
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            try
            {
                renderer.DisposeOffline();
            }
            finally
            {
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }
    }
}
