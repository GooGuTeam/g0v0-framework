// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Audio;
using osu.Framework.Audio.Callbacks;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Video;
using osu.Framework.Platform;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Experimental Windows D3D11/Vulkan recorder. Native encoding runs on a dedicated thread.
    /// Start and stop from outside the draw thread; stop before disposing the host.
    /// </summary>
    public sealed class DesktopVideoRecording
    {
        private readonly GameHost host;
        private readonly VideoRecordingOptions options;
        private readonly string path;
        private readonly Action<EncodedVideoPacket>? packetReceiver;
        private readonly PacketCallback packetCallback;
        private int keyFrameRequested;
        private Exception? packetFailure;
        private readonly AudioManager audioManager;
        private readonly BlockingCollection<GpuCapturedFrame> frames = new BlockingCollection<GpuCapturedFrame>(3);
        private readonly ConcurrentQueue<GpuCapturedFrame> returned = new ConcurrentQueue<GpuCapturedFrame>();
        private readonly TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private GpuFrameCaptureSession session = null!;
        private volatile bool stopping;
        private readonly long start = Stopwatch.GetTimestamp();
        private IntPtr device;
        private bool vulkan;
        private BackendInfoVulkan? vulkanInfo;

        private readonly TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completes after the encoder accepts its first frame; faults if startup fails.
        /// </summary>
        public Task Started => started.Task;

        public Task Completion => completion.Task;
        public ulong EncodedFrames { get; private set; }
        public ulong EncoderPoolDrops { get; private set; }
        public ulong CadenceDrops { get; private set; }
        public long CapturePoolDrops { get; private set; }
        public long WorkerQueueDrops { get; private set; }

        private DesktopVideoRecording(GameHost host, AudioManager audioManager, string path, Action<EncodedVideoPacket>? packetReceiver, VideoRecordingOptions options)
        {
            this.host = host;
            this.options = options;
            this.path = path;
            this.audioManager = audioManager;
            this.packetReceiver = packetReceiver;
            packetCallback = receivePacket;
        }

        public static Task<DesktopVideoRecording> StartAsync(GameHost host, AudioManager audioManager, string path, CancellationToken cancellationToken = default, VideoRecordingOptions? options = null)
            => startAsync(host, audioManager, path, null, cancellationToken, options ?? new VideoRecordingOptions());

        /// <summary>
        /// Starts packet-only encoding. The receiver must enqueue without blocking and
        /// must not perform network I/O. Stop before disposing the receiver or host.
        /// </summary>
        public static Task<DesktopVideoRecording> StartStreamingAsync(GameHost host, AudioManager audioManager, Action<EncodedVideoPacket> receiver, CancellationToken cancellationToken = default, VideoRecordingOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(receiver);
            return startAsync(host, audioManager, string.Empty, receiver, cancellationToken, options ?? new VideoRecordingOptions());
        }

        public void RequestKeyFrame() => Interlocked.Exchange(ref keyFrameRequested, 1);

        private static async Task<DesktopVideoRecording> startAsync(GameHost host, AudioManager audioManager, string path, Action<EncodedVideoPacket>? receiver, CancellationToken cancellationToken, VideoRecordingOptions options)
        {
            ArgumentNullException.ThrowIfNull(audioManager);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            if (File.Exists(path))
                throw new IOException("Recording destination already exists.");
            var recording = new DesktopVideoRecording(host, audioManager, path, receiver, options);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));
            host.DrawThread.Scheduler.Add(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    recording.session = ((IGpuFrameCaptureRenderer)host.Renderer).StartGpuFrameCapture(4);
                    recording.vulkan = recording.session.Device.BackendType == GraphicsBackend.Vulkan;
                    recording.session.MinimumFrameInterval = Stopwatch.Frequency / options.FrameRate;
                    if (recording.vulkan)
                    {
                        recording.vulkanInfo = recording.session.Device.GetVulkanInfo();
                        if (!recording.vulkanInfo.Win32CaptureInteropEnabled)
                            throw new NotSupportedException("Enable G0V0_VULKAN_CAPTURE_INTEROP before starting the client.");
                    }
                    else
                    {
                        recording.device = recording.session.Device.GetD3D11Info().Device;
                        Marshal.ThrowExceptionForHR(g0_record_prepare(recording.device));
                    }
                    recording.session.FrameAvailable += recording.pump;
                    if (!ready.TrySetResult())
                    {
                        recording.session.FrameAvailable -= recording.pump;
                        recording.session.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    recording.session?.Dispose();
                    ready.TrySetException(ex);
                }
            });
            await ready.Task.ConfigureAwait(false);
            new Thread(recording.run) { IsBackground = true, Name = "Video encoding" }.Start();
            return recording;
        }

        private void pump()
        {
            if (session.IsDisposed)
                return;
            while (returned.TryDequeue(out var frame))
                frame.Dispose();
            if (stopping)
            {
                session.Paused = true;
                frames.CompleteAdding();
                return;
            }
            if (!session.TryAcquireFrame(out var captured))
                return;
            if (!frames.TryAdd(captured!))
            {
                WorkerQueueDrops++;
                captured!.Dispose();
            }
        }

        public Task StopAsync()
        {
            if (!Completion.IsCompleted)
            {
                stopping = true;
                host.DrawThread.Scheduler.Add(pump);
            }
            return Completion;
        }

        private void run()
        {
            IntPtr recorder = IntPtr.Zero;
            Exception? failure = null;
            float[] audio = new float[2048 * MixerAudioCapture.Channels];
            MixerAudioCapture? capture = null;
            long videoEndSample = 0;
            uint recordingWidth = 0, recordingHeight = 0;
            try
            {
                capture = audioManager.BeginCapture(start);
                while (!frames.IsCompleted)
                {
                    if (recorder != IntPtr.Zero)
                    {
                        long readySample = (long)((Stopwatch.GetTimestamp() - start) * (double)MixerAudioCapture.SampleRate / Stopwatch.Frequency)
                                           - MixerAudioCapture.SampleRate / 10;
                        writeAudioUntil(recorder, capture, audio, Math.Min(videoEndSample, readySample));
                    }
                    if (!frames.TryTake(out var frame, 20))
                        continue;
                    try
                    {
                        if (recorder != IntPtr.Zero && (frame.Texture.Width != recordingWidth || frame.Texture.Height != recordingHeight))
                            throw new InvalidOperationException("Recording stopped because the window size changed.");
                        if (recorder == IntPtr.Zero)
                        {
                            recordingWidth = frame.Texture.Width;
                            recordingHeight = frame.Texture.Height;
                            if (vulkan)
                            {
                                if (frame.Texture.Format != PixelFormat.B8G8R8A8UNorm)
                                    throw new NotSupportedException("Vulkan recording currently requires BGRA8 UNORM.");
                                if (packetReceiver != null)
                                    Marshal.ThrowExceptionForHR(g0_vk_stream_open_configured(vulkanInfo!.Instance, vulkanInfo.PhysicalDevice, vulkanInfo.Device,
                                        vulkanInfo.GraphicsQueueFamilyIndex, frame.Texture.Width, frame.Texture.Height, MixerAudioCapture.SampleRate, MixerAudioCapture.Channels,
                                        (uint)options.FrameRate, (uint)options.Bitrate, (uint)options.H264Profile,
                                        (uint)options.Width, (uint)options.Height, packetCallback, IntPtr.Zero, out recorder));
                                else
                                    Marshal.ThrowExceptionForHR(g0_vk_open_configured(vulkanInfo!.Instance, vulkanInfo.PhysicalDevice, vulkanInfo.Device,
                                        vulkanInfo.GraphicsQueueFamilyIndex, frame.Texture.Width, frame.Texture.Height, path, MixerAudioCapture.SampleRate, MixerAudioCapture.Channels,
                                        (uint)options.FrameRate, (uint)options.Bitrate, (uint)options.H264Profile, (uint)options.Width, (uint)options.Height, out recorder));
                            }
                            else if (packetReceiver != null)
                                Marshal.ThrowExceptionForHR(g0_stream_open_configured(device, (uint)options.Width, (uint)options.Height,
                                    MixerAudioCapture.SampleRate, MixerAudioCapture.Channels, (uint)options.FrameRate, (uint)options.Bitrate, (uint)options.H264Profile, packetCallback, IntPtr.Zero, out recorder));
                            else
                                Marshal.ThrowExceptionForHR(g0_record_open_configured(device, path, (uint)options.Width, (uint)options.Height,
                                    MixerAudioCapture.SampleRate, MixerAudioCapture.Channels, (uint)options.FrameRate, (uint)options.Bitrate, (uint)options.H264Profile, out recorder));
                        }
                        if (packetFailure != null)
                            throw new InvalidOperationException(@"The encoded packet receiver failed.", packetFailure);
                        if (Interlocked.Exchange(ref keyFrameRequested, 0) != 0)
                        {
                            // Some encoders only honour their configured GOP cadence.
                            if (vulkan) g0_vk_stream_keyframe(recorder);
                            else g0_stream_keyframe(recorder);
                        }
                        long timestamp = (frame.Timestamp - start) * 10000000 / Stopwatch.Frequency;
                        if (vulkan)
                        {
                            Marshal.ThrowExceptionForHR(g0_vk_prepare(recorder, vulkanInfo!.GetVkImage(frame.Texture), out IntPtr command, out IntPtr next, out ulong fence));
                            var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            host.DrawThread.Scheduler.Add(() =>
                            {
                                try { vulkanInfo.SubmitExternalCapture(command, next, fence); submitted.SetResult(); }
                                catch (Exception ex) { submitted.SetException(ex); }
                            });
                            submitted.Task.GetAwaiter().GetResult();
                            Marshal.ThrowExceptionForHR(g0_vk_encode(recorder, timestamp));
                        }
                        else
                            Marshal.ThrowExceptionForHR(g0_record_frame(recorder, session.Device.GetD3D11Info().GetTexturePointer(frame.Texture), timestamp));
                        started.TrySetResult();
                        videoEndSample = (long)((timestamp / 10000000.0 + 1.0 / options.FrameRate) * MixerAudioCapture.SampleRate);
                    }
                    finally
                    {
                        returned.Enqueue(frame);
                    }
                }
                if (recorder != IntPtr.Zero)
                    writeAudioUntil(recorder, capture, audio, videoEndSample);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                stopping = true;
                capture?.Dispose();
                if (recorder != IntPtr.Zero)
                {
                    ulong accepted, poolDrops, cadenceDrops;
                    if (vulkan)
                        g0_vk_stats(recorder, out accepted, out poolDrops, out cadenceDrops);
                    else
                        g0_record_stats(recorder, out accepted, out poolDrops, out cadenceDrops);
                    EncodedFrames = accepted;
                    EncoderPoolDrops = poolDrops;
                    CadenceDrops = cadenceDrops;
                    int result = vulkan ? g0_vk_close(recorder) : g0_record_close(recorder);
                    if (result < 0)
                        failure ??= Marshal.GetExceptionForHR(result);
                }
                Exception? error = failure;
                host.DrawThread.Scheduler.Add(() =>
                {
                    session.Paused = true;
                    session.FrameAvailable -= pump;
                    frames.CompleteAdding();
                    while (frames.TryTake(out var pending))
                        pending.Dispose();
                    while (returned.TryDequeue(out var pending))
                        pending.Dispose();
                    CapturePoolDrops = session.DroppedFrames;
                    session.Dispose();
                    frames.Dispose();
                    if (recorder == IntPtr.Zero && error == null)
                        error = new InvalidOperationException("No video frames were captured.");
                    if (error != null)
                    {
                        started.TrySetException(error);
                        completion.TrySetException(error);
                    }
                    else
                        completion.TrySetResult();
                });
            }
        }

        private void receivePacket(IntPtr context, uint stream, IntPtr data, uint size, long pts, long dts, long duration, uint flags)
        {
            try
            {
                if (size > 16 * 1024 * 1024)
                    throw new InvalidOperationException(@"Encoded packet exceeds the queue limit.");
                byte[] copy = new byte[checked((int)size)];
                Marshal.Copy(data, copy, 0, copy.Length);
                packetReceiver?.Invoke(new EncodedVideoPacket(copy, stream == 0, (flags & 1) != 0, (flags & 2) != 0, pts, dts, duration));
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref packetFailure, ex, null);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PacketCallback(IntPtr context, uint stream, IntPtr data, uint size, long pts, long dts, long duration, uint flags);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_stream_open_configured(IntPtr device, uint width, uint height, uint rate, uint channels, uint fps, uint bitrate, uint profile, PacketCallback callback, IntPtr context, out IntPtr recorder);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vk_stream_open_configured(IntPtr instance, IntPtr physical, IntPtr device, uint family, uint width, uint height, uint rate, uint channels, uint fps, uint bitrate, uint profile, uint outputWidth, uint outputHeight, PacketCallback callback, IntPtr context, out IntPtr recorder);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_stream_keyframe(IntPtr recorder);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vk_stream_keyframe(IntPtr recorder);

        private void writeAudioUntil(IntPtr recorder, MixerAudioCapture capture, float[] buffer, long endSample)
        {
            while (capture.Position < endSample)
            {
                int count = (int)Math.Min(buffer.Length / MixerAudioCapture.Channels, endSample - capture.Position);
                long position = capture.Position;
                capture.Read(buffer, count);
                Marshal.ThrowExceptionForHR(vulkan
                    ? g0_vk_audio(recorder, buffer, (uint)count, position)
                    : g0_record_audio(recorder, buffer, (uint)count, position));
            }
        }

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int g0_vk_open_configured(IntPtr instance, IntPtr physical, IntPtr device, uint family, uint width, uint height, string path, uint rate, uint channels, uint fps, uint bitrate, uint profile, uint outputWidth, uint outputHeight, out IntPtr recorder);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vk_prepare(IntPtr recorder, ulong image, out IntPtr command, out IntPtr next, out ulong fence);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vk_encode(IntPtr recorder, long time);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vk_audio(IntPtr recorder, [In] float[] pcm, uint frames, long position);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vk_close(IntPtr recorder);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_vk_stats(IntPtr recorder, out ulong accepted, out ulong pool, out ulong cadence);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_prepare(IntPtr device);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int g0_record_open_configured(IntPtr device, string path, uint width, uint height, uint rate, uint channels, uint fps, uint bitrate, uint profile, out IntPtr recorder);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_frame(IntPtr recorder, IntPtr texture, long timestamp);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_audio(IntPtr recorder, [In] float[] pcm, uint frames, long position);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_close(IntPtr recorder);
        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_record_stats(IntPtr recorder, out ulong accepted, out ulong poolDrops, out ulong cadenceDrops);
    }
}
