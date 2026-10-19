// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Development;
using osu.Framework.Allocation;
using osu.Framework.Timing;
using osu.Framework.Graphics.Veldrid;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// A private windowless rendering context for an export worker. Create, use and
    /// dispose on one dedicated thread. Do not pass draw nodes or textures from a live host.
    /// </summary>
    public sealed class OfflineRenderContext : IDisposable
    {
        private readonly VeldridRenderer renderer;
        private readonly Vector2 size;
        private readonly int threadId;
        private readonly bool previousDrawThread;
        private ulong sequence;
        private bool disposed;

        public IRenderer Renderer
        {
            get { checkAvailable(); return renderer; }
        }

        public global::Veldrid.GraphicsDevice Device
        {
            get { checkAvailable(); return ((IVeldridRenderer)renderer).Device; }
        }

        public OfflineRenderContext(int width, int height)
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException(@"Offline rendering requires Windows, Linux or macOS.");
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Export size must be even and within device limits.");
            threadId = Environment.CurrentManagedThreadId;
            previousDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            size = new Vector2(width, height);
            renderer = new VeldridRenderer();
            try
            {
                renderer.InitialiseOffline((uint)width, (uint)height);
            }
            catch
            {
                ThreadSafety.IsDrawThread = previousDrawThread;
                throw;
            }
        }

        /// <summary>
        /// Draw an already loaded, updated private scene. This waits for GPU completion on
        /// the export worker only; the caller must finish encoding before reusing the target.
        /// </summary>
        public void LoadScene(Drawable scene, IFrameBasedClock clock, IReadOnlyDependencyContainer dependencies)
        {
            checkAvailable();
            using var scope = LoadErrorHandling.Suppress();
            scene.Load(clock, dependencies);
        }

        public void UpdateScene(Drawable scene)
        {
            checkAvailable();
            bool previous = ThreadSafety.IsUpdateThread;
            ThreadSafety.IsUpdateThread = true;
            using var scope = LoadErrorHandling.Suppress();
            try { scene.UpdateSubTree(); }
            finally { ThreadSafety.IsUpdateThread = previous; }
        }

        public void Render(Drawable scene, OfflineFrameRenderer target)
        {
            checkThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            using var scope = LoadErrorHandling.Suppress();
            DrawNode node;
            bool previous = ThreadSafety.IsUpdateThread;
            ThreadSafety.IsUpdateThread = true;
            try
            {
                scene.UpdateSubTreeMasking();
                node = scene.GenerateDrawNodeSubtree(++sequence, 0, false);
            }
            finally
            {
                ThreadSafety.IsUpdateThread = previous;
            }
            renderer.BeginFrame(size);
            target.Draw(node);
            renderer.FinishFrame();
            renderer.WaitUntilIdle();
        }

        public void Dispose()
        {
            checkThread();
            if (disposed)
                return;
            disposed = true;
            try
            {
                renderer.DisposeOffline();
            }
            finally
            {
                ThreadSafety.IsDrawThread = previousDrawThread;
            }
        }

        private void checkAvailable()
        {
            checkThread();
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        private void checkThread()
        {
            if (Environment.CurrentManagedThreadId != threadId)
                throw new InvalidOperationException("Export context must be used on its creating thread.");
        }
    }
}
