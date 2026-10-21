// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Development;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Copies presented frames into a bounded pool of device-local textures without mapping pixels.
    /// Draw-thread-only. A lease must not be returned until all consumer GPU work has completed.
    /// </summary>
    public sealed class GpuFrameCaptureSession : IDisposable
    {
        public GraphicsDevice Device { get; }
        public long DroppedFrames { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool Paused { get; set; }
        /// <summary>Minimum Stopwatch ticks between GPU copies. Zero captures every presented frame.</summary>
        public long MinimumFrameInterval { get; set; }
        private long nextCopy;
        public event Action? FrameAvailable;

        private readonly int capacity;
        private readonly List<Slot> slots = new List<Slot>();
        private readonly Queue<Slot> pending = new Queue<Slot>();
        private long sequence;

        internal GpuFrameCaptureSession(GraphicsDevice device, int capacity)
        {
            if (capacity < 2 || capacity > 16)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            if (device.BackendType != GraphicsBackend.Direct3D11 && device.BackendType != GraphicsBackend.Vulkan)
                throw new NotSupportedException("GPU capture requires Direct3D11 or Vulkan.");

            Device = device;
            this.capacity = capacity;
        }

        internal void Capture(Texture source)
        {
            ensureDrawThread();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            FrameAvailable?.Invoke();
            if (Paused || IsDisposed)
                return;
            long frameIndex = sequence++;
            long timestamp = Stopwatch.GetTimestamp();
            if (timestamp < nextCopy)
                return;
            // Keep a fixed cadence rather than adding an interval to each actual
            // render timestamp (which can halve capture rate with vsync jitter).
            nextCopy = MinimumFrameInterval > 0
                ? (nextCopy == 0 || timestamp - nextCopy >= MinimumFrameInterval
                    ? timestamp + MinimumFrameInterval
                    : nextCopy + MinimumFrameInterval)
                : timestamp;
            Slot? slot = slots.FirstOrDefault(s => !s.InUse);

            if (slot == null)
            {
                if (slots.Count == capacity)
                {
                    DroppedFrames++;
                    return;
                }

                slot = new Slot(Device);
                slots.Add(slot);
            }

            slot.Prepare(source);
            slot.Commands.Begin();
            slot.Commands.CopyTexture(source, slot.Texture!);
            slot.Commands.End();
            Device.SubmitCommands(slot.Commands, slot.Fence);
            slot.InUse = true;
            slot.FrameIndex = frameIndex;
            slot.Timestamp = timestamp;
            pending.Enqueue(slot);
        }

        /// <summary>
        /// Returns the oldest completed copy without waiting. Timestamp uses Stopwatch ticks,
        /// measured at capture submission, not presentation or encoder completion time.
        /// </summary>
        public bool TryAcquireFrame(out GpuCapturedFrame? frame)
        {
            ensureDrawThread();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            frame = null;
            if (!pending.TryPeek(out Slot? slot) || !slot.Fence.Signaled)
                return false;

            pending.Dequeue();
            slot.Leased = true;
            frame = new GpuCapturedFrame(slot.Texture!, slot.FrameIndex, slot.Timestamp, Device.IsUvOriginTopLeft, () => release(slot));
            return true;
        }

        private void release(Slot slot)
        {
            ensureDrawThread();
            Device.ResetFence(slot.Fence);
            slot.Leased = false;
            slot.InUse = false;
        }

        public void Dispose()
        {
            ensureDrawThread();
            if (IsDisposed)
                return;
            if (slots.Any(s => s.Leased))
                throw new InvalidOperationException("Return all encoder frame leases before stopping capture.");

            // Shutdown only: per-frame capture never waits for the GPU.
            foreach (var slot in slots)
            {
                if (slot.InUse)
                    Device.WaitForFence(slot.Fence);
                slot.Dispose();
            }

            pending.Clear();
            slots.Clear();
            IsDisposed = true;
        }

        private static void ensureDrawThread()
        {
            if (!ThreadSafety.IsDrawThread)
                throw new InvalidOperationException("GPU capture must be controlled on the draw thread.");
        }

        private sealed class Slot : IDisposable
        {
            public readonly CommandList Commands;
            public readonly Fence Fence;
            public Texture? Texture;
            public bool InUse;
            public bool Leased;
            public long FrameIndex;
            public long Timestamp;
            private readonly GraphicsDevice device;

            public Slot(GraphicsDevice device)
            {
                this.device = device;
                Commands = device.ResourceFactory.CreateCommandList();
                Fence = device.ResourceFactory.CreateFence(false);
            }

            public void Prepare(Texture source)
            {
                if (Texture?.Width == source.Width && Texture.Height == source.Height && Texture.Format == source.Format)
                    return;

                Texture?.Dispose();
                Texture = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D(source.Width, source.Height, 1, 1,
                    source.Format, TextureUsage.Sampled | TextureUsage.RenderTarget));
            }

            public void Dispose()
            {
                Texture?.Dispose();
                Commands.Dispose();
                Fence.Dispose();
            }
        }
    }
}
