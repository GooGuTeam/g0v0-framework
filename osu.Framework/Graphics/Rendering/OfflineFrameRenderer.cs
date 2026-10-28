// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Development;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Veldrid.Buffers;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Renders a private scene's draw node into a fixed-size GPU framebuffer, never the
    /// swapchain. Use only on a dedicated export renderer's draw thread. The caller owns
    /// BeginFrame/FinishFrame, simulation timing, GPU completion and encoder leases.
    /// </summary>
    public sealed class OfflineFrameRenderer : IDisposable
    {
        private readonly IRenderer renderer;
        private readonly IFrameBuffer target;
        private readonly RectangleI bounds;
        private bool disposed;

        public OfflineFrameRenderer(IRenderer renderer, int width, int height)
        {
            requireDrawThread();
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentException("Export dimensions must be positive even numbers.");
            this.renderer = renderer;
            bounds = new RectangleI(0, 0, width, height);
            // Paths drawn without framebuffers need depth.
            target = renderer.CreateFrameBuffer(TexturePixelFormat.R8G8B8A8Float, Lines.Path.DirectDrawingEnabled ? new[] { RenderBufferFormat.D16 } : null);
            target.Size = new Vector2(width, height);
        }

        /// <summary>
        /// GPU texture for same-device conversion after FinishFrame and GPU completion.
        /// Do not dispose it, resize it, or draw the next frame until the encoder releases it.
        /// </summary>
        public global::Veldrid.Texture Texture
        {
            get
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (target is not IVeldridFrameBuffer buffer)
                    throw new NotSupportedException("Offline GPU export requires a Veldrid framebuffer.");
                return buffer.Framebuffer.ColorTargets[0].Target;
            }
        }

        public void Draw(DrawNode scene)
        {
            requireDrawThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            target.Bind();
            renderer.PushMaskingInfo(new MaskingInfo
            {
                ScreenSpaceAABB = bounds,
                MaskingRect = new RectangleF(0, 0, bounds.Width, bounds.Height),
                ToMaskingSpace = Matrix3.Identity,
                BlendRange = 1,
                AlphaExponent = 1,
            }, true);
            renderer.PushViewport(bounds);
            renderer.PushScissorState(false);
            renderer.PushOrtho(new RectangleF(0, 0, bounds.Width, bounds.Height));
            renderer.PushDepthInfo(new DepthInfo(false, false));
            try
            {
                renderer.Clear(new ClearInfo(osuTK.Graphics.Color4.Black));
                DrawNode.DrawOther(scene, renderer);
                renderer.FlushCurrentBatch(null);
            }
            finally
            {
                renderer.PopDepthInfo();
                renderer.PopOrtho();
                renderer.PopScissorState();
                renderer.PopViewport();
                renderer.PopMaskingInfo();
                target.Unbind();
            }
        }

        public void Dispose()
        {
            requireDrawThread();
            if (disposed)
                return;
            disposed = true;
            target.Dispose();
        }

        private static void requireDrawThread()
        {
            if (!ThreadSafety.IsDrawThread)
                throw new InvalidOperationException("Offline rendering must run on the export draw thread.");
        }
    }
}
