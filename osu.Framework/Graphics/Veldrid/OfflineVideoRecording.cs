// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Video;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Writes a private, fixed-rate D3D11 scene and privately mixed audio to an MP4 file.
    /// Create, write, complete and dispose on the export context's thread. This encoder may
    /// wait for GPU and encoder completion; never use it on a live host's draw thread.
    /// </summary>
    public sealed class OfflineVideoRecording : IOfflineVideoRecording
    {
        public int FrameRate { get; }
        public static int AudioSampleRate => 48000;
        public static int AudioChannels => 2;
        public int AudioFramesPerVideoFrame => AudioSampleRate / FrameRate;

        private readonly int threadId = Environment.CurrentManagedThreadId;
        private readonly OfflineRenderContext context;
        private readonly uint width;
        private readonly uint height;
        private IntPtr recorder;
        private bool faulted;
        private bool completed;

        public long WrittenFrames { get; private set; }
        public long WrittenAudioFrames => checked(WrittenFrames * AudioFramesPerVideoFrame);

        /// <summary>
        /// Simulation time of the next output frame, in milliseconds, relative to export start.
        /// Use this to advance the private replay clock rather than elapsed wall-clock time.
        /// </summary>
        public double NextFrameTime => WrittenFrames * (1000.0 / FrameRate);

        /// <summary>
        /// The context and its device must outlive this recording. The native GPU video
        /// library and a compatible hardware H.264 encoder must be available.
        /// </summary>
        public OfflineVideoRecording(OfflineRenderContext context, string path, int width, int height, VideoRecordingOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(@"Offline video encoding currently requires Windows D3D11.");
            if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (width & 1) != 0 || (height & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(width), @"Export dimensions must be even and within device limits.");
            if (context.Device.BackendType != GraphicsBackend.Direct3D11)
                throw new NotSupportedException(@"Offline video encoding requires a private D3D11 context.");

            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(path), @".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(@"The recording destination must be an MP4 file.", nameof(path));
            if (File.Exists(path))
                throw new IOException(@"Recording destination already exists.");

            options ??= new VideoRecordingOptions();
            FrameRate = options.FrameRate;
            this.context = context;
            this.width = (uint)width;
            this.height = (uint)height;

            IntPtr device = context.Device.GetD3D11Info().Device;
            Marshal.ThrowExceptionForHR(g0_record_prepare(device));
            Marshal.ThrowExceptionForHR(g0_record_open_configured(device, path, this.width, this.height,
                (uint)AudioSampleRate, (uint)AudioChannels, (uint)options.FrameRate, (uint)options.Bitrate, (uint)options.H264Profile, out recorder));

            // Nothing is pending yet; this fails now, rather than after the whole export, if the native library is outdated.
            Marshal.ThrowExceptionForHR(g0_record_offline_flush(recorder));
        }

        /// <summary>
        /// Submits one completed GPU frame and its corresponding stereo float sample frames.
        /// Advance and mix the private scene for this interval before calling. The source
        /// texture can be reused on return; native encoder surfaces have separate leases.
        /// The frame reaches the encoder with the next call or <see cref="Complete"/>, once its conversion has finished.
        /// An encoding failure makes the recording unusable and must abort the export.
        /// </summary>
        public void WriteFrame(Texture texture, float[] audio)
        {
            checkThread();
            ensureWritable();
            ArgumentNullException.ThrowIfNull(texture);
            ArgumentNullException.ThrowIfNull(audio);
            if (texture.Width != width || texture.Height != height || texture.SampleCount != TextureSampleCount.Count1)
                throw new ArgumentException(@"The source texture must match the export size and must not be multisampled.", nameof(texture));
            if (audio.Length != AudioFramesPerVideoFrame * AudioChannels)
                throw new ArgumentException(@"Audio must cover exactly one video frame at the configured frame rate.", nameof(audio));

            long audioPosition = WrittenAudioFrames;
            try
            {
                IntPtr source = context.Device.GetD3D11Info().GetTexturePointer(texture);
                int result = g0_record_offline_frame(recorder, source, WrittenFrames);
                Marshal.ThrowExceptionForHR(result);
                if (result != 0)
                    throw new InvalidOperationException(@"The offline encoder did not accept the video frame.");
                Marshal.ThrowExceptionForHR(g0_record_audio(recorder, audio, (uint)AudioFramesPerVideoFrame, audioPosition));
                WrittenFrames++;
            }
            catch
            {
                faulted = true;
                throw;
            }
        }

        /// <summary>
        /// Finalises the MP4 and verifies that no frame was dropped. Call explicitly to
        /// observe finalisation errors. Dispose alone only releases native resources.
        /// </summary>
        public void Complete()
        {
            checkThread();
            if (completed)
                return;
            ensureWritable();
            if (WrittenFrames == 0)
                throw new InvalidOperationException(@"Cannot complete an export without video frames.");

            try
            {
                Marshal.ThrowExceptionForHR(g0_record_offline_flush(recorder));
                g0_record_stats(recorder, out ulong accepted, out ulong poolDrops, out ulong cadenceDrops);
                if (accepted != (ulong)WrittenFrames || poolDrops != 0 || cadenceDrops != 0)
                    throw new InvalidOperationException($@"The offline encoder did not preserve every submitted frame ({accepted} of {WrittenFrames} accepted, {poolDrops} pool and {cadenceDrops} cadence drops).");
                IntPtr closing = recorder;
                recorder = IntPtr.Zero;
                Marshal.ThrowExceptionForHR(g0_record_close(closing));
                completed = true;
            }
            catch
            {
                faulted = true;
                throw;
            }
        }

        public void Dispose()
        {
            checkThread();
            if (recorder == IntPtr.Zero)
                return;
            IntPtr closing = recorder;
            recorder = IntPtr.Zero;
            // Cleanup must not replace the original export failure with a finalisation failure.
            g0_record_close(closing);
        }

        private void ensureWritable()
        {
            ObjectDisposedException.ThrowIf(recorder == IntPtr.Zero, this);
            if (faulted)
                throw new InvalidOperationException(@"The export failed and cannot accept further frames.");
        }

        private void checkThread()
        {
            if (Environment.CurrentManagedThreadId != threadId)
                throw new InvalidOperationException(@"Offline encoding must run on its creating export thread.");
        }

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_prepare(IntPtr device);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int g0_record_open_configured(IntPtr device, string path, uint width, uint height, uint rate, uint channels,
            uint fps, uint bitrate, uint profile, out IntPtr recorder);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_offline_frame(IntPtr recorder, IntPtr texture, long index);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_offline_flush(IntPtr recorder);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_audio(IntPtr recorder, [In] float[] pcm, uint frames, long position);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_record_stats(IntPtr recorder, out ulong accepted, out ulong poolDrops, out ulong cadenceDrops);

        [DllImport("g0v0-gpu-video", CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_record_close(IntPtr recorder);
    }
}
