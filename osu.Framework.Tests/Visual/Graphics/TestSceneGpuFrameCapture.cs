// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Platform;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Graphics
{
    public partial class TestSceneGpuFrameCapture : FrameworkTestScene
    {
        [Resolved]
        private GameHost host { get; set; } = null!;

        private GpuFrameCaptureSession? session;
        private int completed;

        /// <summary>
        /// Whether the renderer in use cannot capture frames at all. Headless test runs use <c>DummyRenderer</c>, which does not implement
        /// <see cref="IGpuFrameCaptureRenderer"/>; the steps below degrade to no-ops rather than casting and failing the whole test.
        /// </summary>
        private bool unsupported;

        public TestSceneGpuFrameCapture()
        {
            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.CornflowerBlue });
            AddStep("start GPU capture (D3D11/Vulkan only)", () =>
            {
                // resolved here rather than on the draw thread, so that the outcome is already settled by the time the assertion below runs.
                if (host.Renderer is not IGpuFrameCaptureRenderer renderer)
                {
                    unsupported = true;
                    return;
                }

                host.DrawThread.Scheduler.Add(() =>
                {
                    session?.Dispose();
                    session = renderer.StartGpuFrameCapture(3);
                    Interlocked.Exchange(ref completed, 0);
                });
            });
            AddStep("poll completed frame", () => host.DrawThread.Scheduler.Add(() =>
            {
                if (session?.TryAcquireFrame(out var frame) == true)
                {
                    frame!.Dispose();
                    Interlocked.Increment(ref completed);
                }
            }));
            AddAssert("received GPU frame", () => unsupported || Volatile.Read(ref completed) > 0);
            AddStep("stop GPU capture", () => host.DrawThread.Scheduler.Add(() =>
            {
                session?.Dispose();
                session = null;
            }));
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing && host != null)
                host.DrawThread.Scheduler.Add(() =>
                {
                    session?.Dispose();
                    session = null;
                });
            base.Dispose(isDisposing);
        }
    }
}
