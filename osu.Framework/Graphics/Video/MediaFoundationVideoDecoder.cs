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
    /// Decodes video with Windows Media Foundation through the native GPU video library, so Windows needs no FFmpeg for
    /// the formats it supports. Streams Media Foundation cannot open fall back to the FFmpeg implementation if FFmpeg is present;
    /// Windows x64 packages omit it (see <c>native/WindowsFfmpegExclusion.targets</c>), so such videos do not play there.
    /// </summary>
    /// <remarks>
    /// Media Foundation's software decoders are used; frames are converted from NV12 to the I420 planes video textures expect.
    /// Disable the UseMediaFoundationVideoDecoder framework setting to always use FFmpeg.
    /// </remarks>
    internal sealed unsafe class MediaFoundationVideoDecoder : VideoDecoder
    {
        private const int max_pending_frames = 3;

        // MF_E_INVALIDMEDIATYPE: the frame size grew beyond the buffers passed in.
        private const int format_changed = unchecked((int)0xC00D36B4);

        // FFmpeg.AutoGen loads its libraries by these names; Windows x64 packages normally omit them.
        private static readonly Lazy<bool> ffmpeg_available = new Lazy<bool>(() =>
        {
            if (!NativeLibrary.TryLoad("avcodec-58", typeof(VideoDecoder).Assembly, null, out IntPtr library))
                return false;

            NativeLibrary.Free(library);
            return true;
        });

        public static bool IsAvailable => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
                                                                    && FrameworkFeatureFlags.UseMediaFoundationVideoDecoder;

        public override double Duration => useFfmpeg ? base.Duration : info.Duration / 10000.0;
        public override double FrameRate => useFfmpeg ? base.FrameRate : info.FrameRate;
        public override bool CanSeek => useFfmpeg ? base.CanSeek : info.CanSeek != 0;
        public override DecoderState State => useFfmpeg ? base.State : state;
        public override float LastDecodedFrameTime => useFfmpeg ? base.LastDecodedFrameTime : lastDecodedFrameTime;

        /// <summary>
        /// Whether Media Foundation opened the stream, rather than the FFmpeg fallback.
        /// </summary>
        internal bool UsesMediaFoundation => !useFfmpeg && decoder != IntPtr.Zero;

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
        private bool useFfmpeg;
        private bool disposeStarted;
        private bool disposed;
        private volatile DecoderState state = DecoderState.Ready;
        private volatile float lastDecodedFrameTime;
        private double? skipOutputUntilTime;

        // Like FFmpeg's stream start time: the first frame's timestamp, so that frame times start at zero.
        private long? startTime;
        private Task? decodingTask;
        private CancellationTokenSource? decodingCancellation;

        public MediaFoundationVideoDecoder(IRenderer renderer, Stream videoStream)
            : base(renderer, videoStream)
        {
            this.renderer = renderer;
            stream = videoStream;
            handle = GCHandle.Alloc(this, GCHandleType.Weak);
        }

        public override void StartDecoding()
        {
            if (useFfmpeg)
            {
                base.StartDecoding();
                return;
            }

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
                    if (!ffmpeg_available.Value)
                    {
                        Logger.Log($"Media Foundation cannot decode this video, and FFmpeg is not available: {e.Message}");
                        state = DecoderState.Faulted;
                        return;
                    }

                    Logger.Log($"Media Foundation cannot decode this video, falling back to FFmpeg: {e.Message}");
                    useFfmpeg = true;
                    if (stream.CanSeek)
                        stream.Position = 0;
                    base.StartDecoding();
                    return;
                }
            }

            decodingCancellation = new CancellationTokenSource();
            var token = decodingCancellation.Token;
            decodingTask = Task.Factory.StartNew(() => decodingLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public override Task StopDecodingAsync()
        {
            if (useFfmpeg)
                return base.StopDecodingAsync();

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
            if (useFfmpeg)
            {
                base.Seek(targetTimestamp);
                return;
            }

            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            commands.Enqueue(() => seek(targetTimestamp));
        }

        public override void ReturnFrames(IEnumerable<DecodedFrame> frames)
        {
            if (useFfmpeg)
            {
                base.ReturnFrames(frames);
                return;
            }

            foreach (var f in frames)
            {
                f.Texture.FlushUploads();
                availableTextures.Enqueue(f.Texture);
            }
        }

        public override IEnumerable<DecodedFrame> GetDecodedFrames()
        {
            if (useFfmpeg)
                return base.GetDecodedFrames();

            var frames = new List<DecodedFrame>(decodedFrames.Count);
            while (decodedFrames.TryDequeue(out var frame))
                frames.Add(frame);

            return frames;
        }

        public override Matrix3 GetConversionMatrix()
        {
            if (useFfmpeg)
                return base.GetConversionMatrix();

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
            int result = g0_vdec_open(&readStream, GCHandle.ToIntPtr(handle), stream.Length, out decoder, out info);
            if (result < 0)
            {
                decoder = IntPtr.Zero;
                Marshal.ThrowExceptionForHR(result);
            }

            Logger.Log($"Decoding video with Media Foundation: {info.Width}x{info.Height}, {info.FrameRate:0.##} fps");
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
            if (disposeStarted)
                return;

            disposeStarted = true;

            // Stop decoding before the base class disposes the stream.
            StopDecodingAsync().ContinueWith(_ =>
            {
                lock (streamLock)
                    disposed = true;

                if (decoder != IntPtr.Zero)
                {
                    g0_vdec_close(decoder);
                    decoder = IntPtr.Zero;
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

                handle.Free();
                base.Dispose(disposing);
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
        }

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_open(delegate* unmanaged[Cdecl]<IntPtr, long, byte*, int, int> read, IntPtr context, long length, out IntPtr decoder, out DecoderInfo info);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_read(IntPtr decoder, IntPtr y, IntPtr u, IntPtr v, int capacityWidth, int capacityHeight, out long time, ref DecoderInfo info);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vdec_seek(IntPtr decoder, long time);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_vdec_close(IntPtr decoder);
    }
}
