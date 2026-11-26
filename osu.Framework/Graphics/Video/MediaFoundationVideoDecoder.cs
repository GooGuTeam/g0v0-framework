// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Configuration;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osuTK;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Decodes video with Windows Media Foundation through the native GPU video library, so Windows x64 needs no FFmpeg.
    /// Streams Media Foundation cannot open fault the decoder; Windows x64 packages omit FFmpeg
    /// (see <c>native/WindowsFfmpegExclusion.targets</c>), so such videos do not play there.
    /// </summary>
    /// <remarks>
    /// Unless <see cref="VideoDecoder.TargetHardwareVideoDecoders"/> is <see cref="HardwareVideoDecoder.None"/>, decoding uses DXVA on a
    /// private D3D11 device (Media Foundation still decodes codecs the GPU lacks in software). Frames are read back and converted
    /// from NV12 to the I420 planes video textures expect. With <see cref="FrameworkSetting.ZeroCopyVideoDecoding"/> (on by default) on a
    /// Direct3D 11 or Vulkan renderer, frames stay on the GPU instead (see <see cref="MediaFoundationSharedVideo"/>).
    /// Disabling the UseMediaFoundationVideoDecoder framework setting uses FFmpeg instead, which Windows x64 builds only ship
    /// with <c>-p:G0V0KeepWindowsFfmpeg=true</c>.
    /// </remarks>
    internal sealed unsafe class MediaFoundationVideoDecoder : VideoDecoder
    {
        private const int max_pending_frames = 3;

        // MF_E_INVALIDMEDIATYPE: the frame size grew beyond the buffers passed in.
        private const int format_changed = unchecked((int)0xC00D36B4);

        public static bool IsAvailable => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                                                                    && FrameworkFeatureFlags.UseMediaFoundationVideoDecoder;

        public override double Duration => info.Duration / 10000.0;
        public override double FrameRate => info.FrameRate;
        public override bool CanSeek => info.CanSeek != 0;
        public override DecoderState State => state;
        public override float LastDecodedFrameTime => lastDecodedFrameTime;

        private readonly IRenderer renderer;
        private readonly Stream stream;
        private readonly object streamLock = new object();
        private readonly ConcurrentQueue<DecodedFrame> decodedFrames = new ConcurrentQueue<DecodedFrame>();
        private readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        private readonly ConcurrentQueue<Texture> availableTextures = new ConcurrentQueue<Texture>();
        private readonly ConcurrentQueue<PlaneBuffer> availableBuffers = new ConcurrentQueue<PlaneBuffer>();

        private GCHandle handle;
        private IntPtr decoder;
        private DecoderInfo info;
        private bool disposeStarted;
        private bool disposed;
        private bool loggedDecoding;

        // Zero-copy output. The lock orders creating and dropping it against Dispose, and slot releases from the draw thread against closing the native decoder.
        private MediaFoundationSharedVideo? shared;

        // Set once the renderer could not take a shared frame; decoding then reads frames back for the rest of the video.
        private volatile bool sharedFailed;

        /// <summary>
        /// Whether frames stay on the GPU (zero-copy decoding); known once decoding has started.
        /// </summary>
        internal bool UsesSharedFrames => shared != null && !sharedFailed;

        private readonly Lock sharedLock = new Lock();
        private volatile DecoderState state = DecoderState.Ready;
        private volatile float lastDecodedFrameTime;
        private double? skipOutputUntilTime;

        // Like FFmpeg's stream start time: the first frame's timestamp, so that frame times start at zero.
        private long? startTime;
        private Task? decodingTask;
        private CancellationTokenSource? decodingCancellation;

        public MediaFoundationVideoDecoder(IRenderer renderer, Stream videoStream)
        {
            if (!videoStream.CanRead)
                throw new InvalidOperationException($"The given stream does not support reading. A stream used for a {nameof(MediaFoundationVideoDecoder)} must support reading.");

            this.renderer = renderer;
            stream = videoStream;
            handle = GCHandle.Alloc(this, GCHandleType.Weak);
        }

        public override void StartDecoding()
        {
            if (decodingTask != null)
                throw new InvalidOperationException($"Cannot start decoding once already started. Call {nameof(StopDecodingAsync)} first.");

            if (decoder == IntPtr.Zero)
            {
                try
                {
                    open();
                }
                catch (Exception e)
                {
                    Logger.Log($"Media Foundation cannot decode this video: {e.Message}");
                    state = DecoderState.Faulted;
                    return;
                }
            }

            decodingCancellation = new CancellationTokenSource();
            var token = decodingCancellation.Token;
            decodingTask = Task.Factory.StartNew(() => decodingLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public override Task StopDecodingAsync()
        {
            if (decodingTask == null)
                return Task.CompletedTask;

            decodingCancellation!.Cancel();

            return decodingTask.ContinueWith(_ =>
            {
                decodingTask = null;
                decodingCancellation!.Dispose();
                decodingCancellation = null;
                state = DecoderState.Ready;
            });
        }

        public override void Seek(double targetTimestamp)
        {
            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            commands.Enqueue(() => seek(targetTimestamp));
        }

        public override void ReturnFrames(IEnumerable<DecodedFrame> frames)
        {
            foreach (var f in frames)
            {
                f.Texture.FlushUploads();
                availableTextures.Enqueue(f.Texture);
            }
        }

        public override IEnumerable<DecodedFrame> GetDecodedFrames()
        {
            var frames = new List<DecodedFrame>(decodedFrames.Count);
            while (decodedFrames.TryDequeue(out var frame))
                frames.Add(frame);

            return frames;
        }

        public override Matrix3 GetConversionMatrix()
        {
            if (decoder == IntPtr.Zero)
                return Matrix3.Zero;

            // Media Foundation reports MFVideoTransferMatrix: 1 is BT.709, 2 is BT.601. When unspecified, follow the FFmpeg decoder's size rule.
            bool hdtv = info.Matrix == 1 || (info.Matrix != 2 && (info.Width >= 704 || info.Height >= 576));

            return hdtv
                ? new Matrix3(1.164f, 1.164f, 1.164f,
                    0.000f, -0.213f, 2.112f,
                    1.793f, -0.533f, 0.000f)
                : new Matrix3(1.164f, 1.164f, 1.164f,
                    0.000f, -0.392f, 2.017f,
                    1.596f, -0.813f, 0.000f);
        }

        private void open()
        {
            if (!stream.CanSeek)
                throw new NotSupportedException("The stream cannot seek.");

            stream.Position = 0;
            bool hardware = TargetHardwareVideoDecoders.Value != HardwareVideoDecoder.None;
            long adapter = 0;

            // Under the lock, so that Dispose either sees the consumers or prevents them from being created.
            lock (sharedLock)
            {
                if (hardware && FrameworkFeatureFlags.ZeroCopyVideoDecoding && !disposeStarted)
                    shared = MediaFoundationSharedVideo.TryCreate(renderer, out adapter);
            }

            int result = g0_vdec_open(&readStream, GCHandle.ToIntPtr(handle), stream.Length, hardware ? 1 : 0, shared != null ? &adapter : null, shared != null ? 1 : 0,
                out decoder, out info);

            if (result < 0)
            {
                decoder = IntPtr.Zero;
                discardShared();
                Marshal.ThrowExceptionForHR(result);
            }

            if (shared != null && info.Shared == 0)
            {
                Logger.Log("Zero-copy video decoding is unavailable; reading frames back instead");
                discardShared();
            }

            Logger.Log($"Decoding video with Media Foundation: {info.Width}x{info.Height}, {info.FrameRate:0.##} fps");
        }

        /// <summary>
        /// Drops the renderer's side before any frame was shared. It has no consumers yet, so this is safe off the draw thread.
        /// </summary>
        private void discardShared()
        {
            lock (sharedLock)
            {
                shared?.Dispose();
                shared = null;
            }
        }

        private void decodingLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    switch (state)
                    {
                        case DecoderState.Ready:
                        case DecoderState.Running:
                            if (decodedFrames.Count < max_pending_frames)
                                decodeNextFrame();
                            else
                            {
                                // Wait until existing frames are consumed.
                                state = DecoderState.Ready;
                                Thread.Sleep(1);
                            }

                            break;

                        case DecoderState.EndOfStream:
                            // A seek changes the state, allowing decoding to start again.
                            Thread.Sleep(50);
                            break;
                    }

                    while (!cancellationToken.IsCancellationRequested && commands.TryDequeue(out var command))
                        command();
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "Media Foundation video decoder faulted");
                state = DecoderState.Faulted;
            }
            finally
            {
                if (state != DecoderState.Faulted)
                    state = DecoderState.Stopped;
            }
        }

        private void decodeNextFrame()
        {
            if (UsesSharedFrames)
            {
                decodeNextSharedFrame();
                return;
            }

            int width = info.Width, height = info.Height;
            if (!availableBuffers.TryDequeue(out var buffer) || buffer.Width != width || buffer.Height != height)
            {
                buffer?.Dispose();
                buffer = new PlaneBuffer(width, height);
            }

            int result = g0_vdec_read(decoder, buffer.Y, buffer.U, buffer.V, width, height, out long time, ref info);

            if (result != 0)
            {
                availableBuffers.Enqueue(buffer);

                if (result == 1)
                {
                    if (Looping)
                        seek(0);
                    else
                        state = DecoderState.EndOfStream;
                }
                else if (result == format_changed && (info.Width != width || info.Height != height))
                {
                    // Textures and buffers of the old size are replaced as they come back; the frame that changed size is dropped.
                    Logger.Log($"Video size changed to {info.Width}x{info.Height}");
                }
                else
                    Marshal.ThrowExceptionForHR(result);

                return;
            }

            state = DecoderState.Running;

            if (!loggedDecoding)
            {
                loggedDecoding = true;
                Logger.Log($"Media Foundation decodes this video {(info.Hardware != 0 ? "on the GPU (DXVA)" : "in software")}");
            }

            // A format change reported with this frame can shrink it within the buffer; the buffer is replaced when it comes back.
            width = info.Width;
            height = info.Height;

            startTime ??= time;
            double frameTime = (time - startTime.Value) / 10000.0;

            if (skipOutputUntilTime > frameTime)
            {
                availableBuffers.Enqueue(buffer);
                return;
            }

            skipOutputUntilTime = null;
            lastDecodedFrameTime = (float)frameTime;

            Texture? texture;
            while (availableTextures.TryDequeue(out texture) && (texture.Width != width || texture.Height != height))
                texture.Dispose();
            texture ??= renderer.CreateVideoTexture(width, height);

            var upload = new VideoTextureUpload(width, height, new[] { buffer.Y, buffer.U, buffer.V }, () => returnBuffer(buffer));

            // Videos with transparency are not supported, so frames are always opaque.
            texture.SetData(upload, Opacity.Opaque);
            decodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = texture });
        }

        private void decodeNextSharedFrame()
        {
            int result = g0_vdec_read_shared(decoder, out var frame, out long time, ref info);

            switch (result)
            {
                case share_busy:
                    // Every slot is still queued for upload; they come back as the renderer uploads frames.
                    Thread.Sleep(1);
                    return;

                case 1:
                    if (Looping)
                        seek(0);
                    else
                        state = DecoderState.EndOfStream;
                    return;

                case < 0:
                    // Reading back reports the error again if it is not specific to shared output.
                    giveUpSharedFrames($"0x{result:X8}");
                    return;
            }

            state = DecoderState.Running;

            if (!loggedDecoding)
            {
                loggedDecoding = true;
                Logger.Log($"Media Foundation decodes this video {(info.Hardware != 0 ? "on the GPU (DXVA)" : "in software")}, without copying frames to the CPU");
            }

            int width = info.Width, height = info.Height;

            startTime ??= time;
            double frameTime = (time - startTime.Value) / 10000.0;

            if (skipOutputUntilTime > frameTime)
            {
                ReleaseSharedFrame(frame, false);
                return;
            }

            skipOutputUntilTime = null;
            lastDecodedFrameTime = (float)frameTime;

            Texture? texture;
            while (availableTextures.TryDequeue(out texture) && (texture.Width != width || texture.Height != height))
                texture.Dispose();
            texture ??= renderer.CreateVideoTexture(width, height);

            // Videos with transparency are not supported, so frames are always opaque.
            texture.SetData(new MediaFoundationSharedVideo.Upload(this, frame, width, height), Opacity.Opaque);
            decodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = texture });
        }

        /// <summary>
        /// Copies a shared frame into a video texture's planes, on the draw thread.
        /// </summary>
        internal bool CopySharedFrame(MediaFoundationSharedVideo.SharedFrame frame, int width, int height, global::Veldrid.Texture y, global::Veldrid.Texture u,
                                      global::Veldrid.Texture v)
        {
            if (sharedFailed)
                return false;

            try
            {
                return shared?.Copy(frame, width, height, y, u, v) ?? false;
            }
            catch (Exception e)
            {
                giveUpSharedFrames(e.Message);
                return false;
            }
        }

        /// <summary>
        /// Switches to reading frames back after zero-copy decoding failed. Frames already handed out are dropped as they are uploaded.
        /// </summary>
        private void giveUpSharedFrames(string reason)
        {
            if (sharedFailed)
                return;

            sharedFailed = true;
            Logger.Log($"Zero-copy video decoding failed ({reason}); reading frames back instead");
        }

        /// <summary>
        /// Returns a shared frame's slot to the decoder, whether or not it was copied.
        /// </summary>
        internal void ReleaseSharedFrame(MediaFoundationSharedVideo.SharedFrame frame, bool copied)
        {
            lock (sharedLock)
            {
                if (decoder == IntPtr.Zero)
                    return;

                int result = g0_vdec_release_shared(decoder, frame.Generation, frame.Slot, copied ? 1 : 0);
                if (result < 0)
                    Logger.Log($"Could not return a shared video frame (0x{result:X8})");
            }
        }

        private void seek(double targetTimestamp)
        {
            Marshal.ThrowExceptionForHR(g0_vdec_seek(decoder, (long)(targetTimestamp * 10000) + (startTime ?? 0)));
            skipOutputUntilTime = targetTimestamp;
            state = DecoderState.Ready;
        }

        private void returnBuffer(PlaneBuffer buffer)
        {
            if (disposed)
                buffer.Dispose();
            else
                availableBuffers.Enqueue(buffer);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int readStream(IntPtr context, long position, byte* buffer, int size)
        {
            try
            {
                if (GCHandle.FromIntPtr(context).Target is not MediaFoundationVideoDecoder target)
                    return -1;

                lock (target.streamLock)
                {
                    if (target.disposed)
                        return -1;

                    target.stream.Position = position;
                    return target.stream.Read(new Span<byte>(buffer, size));
                }
            }
            catch
            {
                return -1;
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (sharedLock)
            {
                if (disposeStarted)
                    return;

                disposeStarted = true;

                // The consumers belong to the renderer's device: close them on the draw thread, where they copy frames, and
                // before an offline renderer destroys its device (it drains scheduled disposals first). Closing the decoder
                // below does not affect them, as they hold their own references to the shared textures.
                if (shared != null)
                    renderer.ScheduleDisposal(static s => s.Dispose(), shared);
            }

            // Stop decoding before disposing the stream.
            StopDecodingAsync().ContinueWith(_ =>
            {
                lock (streamLock)
                    disposed = true;

                lock (sharedLock)
                {
                    if (decoder != IntPtr.Zero)
                    {
                        g0_vdec_close(decoder);
                        decoder = IntPtr.Zero;
                    }
                }

                while (decodedFrames.TryDequeue(out var frame))
                {
                    frame.Texture.FlushUploads();
                    frame.Texture.Dispose();
                }

                while (availableTextures.TryDequeue(out var texture))
                    texture.Dispose();

                while (availableBuffers.TryDequeue(out var buffer))
                    buffer.Dispose();

                stream.Dispose();
                handle.Free();
            });
        }

        /// <summary>
        /// Native memory for one frame's I420 planes, reused across frames of the same size.
        /// </summary>
        private sealed class PlaneBuffer : IDisposable
        {
            public readonly int Width;
            public readonly int Height;
            public readonly IntPtr Y;
            public readonly IntPtr U;
            public readonly IntPtr V;

            public PlaneBuffer(int width, int height)
            {
                Width = width;
                Height = height;
                int chroma = (width + 1) / 2 * ((height + 1) / 2);
                Y = (IntPtr)NativeMemory.Alloc((nuint)(width * height + chroma * 2));
                U = Y + width * height;
                V = U + chroma;
            }

            public void Dispose() => NativeMemory.Free((void*)Y);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DecoderInfo
        {
            public int Width;
            public int Height;
            public double FrameRate;
            public long Duration;
            public int Matrix;
            public int CanSeek;
            public int Hardware;
            public int Shared;
        }

        // g0_vdec_read_shared: every slot is still with the renderer.
        private const int share_busy = 2;

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_open(delegate* unmanaged[Cdecl]<IntPtr, long, byte*, int, int> read, IntPtr context, long length, int hardware, long* adapter,
                                               int share, out IntPtr decoder, out DecoderInfo info);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_read(IntPtr decoder, IntPtr y, IntPtr u, IntPtr v, int capacityWidth, int capacityHeight, out long time, ref DecoderInfo info);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_read_shared(IntPtr decoder, out MediaFoundationSharedVideo.SharedFrame frame, out long time, ref DecoderInfo info);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_release_shared(IntPtr decoder, int generation, int slot, int consumed);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_seek(IntPtr decoder, long time);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_vdec_close(IntPtr decoder);
    }
}
