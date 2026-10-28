// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Rendering;
using Veldrid;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Worker-owned CPU-readback export using an installed FFmpeg with libx264.
    /// PCM is spooled to disk and muxed after video encoding to avoid two-input pipe deadlocks.
    /// No network destination, shell or unbounded frame queue is used.
    /// </summary>
    /// <remarks>
    /// Readback is pipelined: each frame is copied into one of <see cref="readback_depth"/> staging textures and read
    /// <see cref="readback_depth"/> - 1 frames later, so the export thread does not wait for the GPU after every frame.
    /// A dedicated thread writes frames to FFmpeg, bounded by <see cref="pipe_buffers"/> pixel buffers.
    /// </remarks>
    public sealed class FfmpegOfflineVideoRecording : IOfflineVideoRecording
    {
        private const int readback_depth = 3;
        private const int pipe_buffers = 3;
        private const int stderr_tail_length = 2048;

        private static readonly TimeSpan pipe_timeout = TimeSpan.FromSeconds(30);

        private readonly GraphicsDevice device;
        private readonly VideoRecordingOptions options;
        private readonly string executable;
        private readonly string destination;
        private readonly string videoPath;
        private readonly string audioPath;
        private readonly CancellationToken cancellationToken;
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private readonly byte[] audioBytes;
        private readonly Texture?[] staging = new Texture?[readback_depth];
        private readonly CommandList?[] commands = new CommandList?[readback_depth];
        private readonly Fence?[] fences = new Fence?[readback_depth];
        private readonly Queue<int> pendingReadbacks = new Queue<int>();
        private readonly BlockingCollection<byte[]> freeBuffers = new BlockingCollection<byte[]>(pipe_buffers);
        private readonly BlockingCollection<byte[]> filledBuffers = new BlockingCollection<byte[]>(pipe_buffers);
        private FileStream? audioFile;
        private Process? encoder;
        private Task<string>? stderrTail;
        private Thread? pipeWriter;
        private Exception? pipeFailure;
        private CancellationTokenRegistration cancellationRegistration;
        private bool completed;
        private bool disposed;
        private bool faulted;

        public long WrittenFrames { get; private set; }
        public long WrittenAudioFrames => checked(WrittenFrames * AudioFramesPerVideoFrame);
        public int AudioFramesPerVideoFrame => 48000 / options.FrameRate;

        public FfmpegOfflineVideoRecording(OfflineRenderContext context, string path, VideoRecordingOptions options,
                                          string executable, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executable);
            this.options = options;
            this.executable = executable;
            this.cancellationToken = cancellationToken;
            device = context.Device;
            destination = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(destination), @".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(@"FFmpeg export requires an MP4 destination.", nameof(path));
            if (File.Exists(destination))
                throw new IOException(@"The export destination already exists.");
            videoPath = destination + $@".{Guid.NewGuid():N}.video.mp4";
            audioPath = destination + $@".{Guid.NewGuid():N}.audio.f32";
            audioBytes = new byte[AudioFramesPerVideoFrame * 2 * sizeof(float)];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < readback_depth; i++)
                {
                    staging[i] = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D((uint)options.Width, (uint)options.Height,
                        1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
                    commands[i] = device.ResourceFactory.CreateCommandList();
                    fences[i] = device.ResourceFactory.CreateFence(false);
                }

                for (int i = 0; i < pipe_buffers; i++)
                    freeBuffers.Add(new byte[checked(options.Width * options.Height * 4)], cancellationToken);

                audioFile = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
                var info = createStartInfo();
                add(info, @"-f", @"rawvideo", @"-pixel_format", @"rgba", @"-video_size", $@"{options.Width}x{options.Height}",
                    @"-framerate", number(options.FrameRate), @"-i", @"pipe:0", @"-an", @"-c:v", @"libx264", @"-preset", @"veryfast",
                    @"-b:v", number(options.Bitrate), @"-pix_fmt", @"yuv420p", @"-g", number(options.FrameRate * 2));
                if (options.H264Profile != 0)
                    add(info, @"-profile:v", options.H264Profile == 66 ? @"baseline" : options.H264Profile == 77 ? @"main" : @"high");
                add(info, videoPath);
                encoder = Process.Start(info) ?? throw new InvalidOperationException(@"Unable to start FFmpeg.");
                stderrTail = readTailAsync(encoder.StandardError);
                cancellationRegistration = cancellationToken.Register(() => kill(encoder));
                pipeWriter = new Thread(() => writePipe(encoder.StandardInput.BaseStream)) { IsBackground = true, Name = @"FFmpeg video pipe" };
                pipeWriter.Start();
            }
            catch
            {
                try { Dispose(); }
                catch (Exception) { }
                throw;
            }
        }

        public void WriteFrame(Texture texture, float[] audio)
        {
            checkThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed || faulted)
                throw new InvalidOperationException(@"The FFmpeg export cannot accept further frames.");
            cancellationToken.ThrowIfCancellationRequested();
            if (texture.Width != options.Width || texture.Height != options.Height || texture.Format != PixelFormat.R8G8B8A8UNorm
                || texture.SampleCount != TextureSampleCount.Count1 || audio.Length * sizeof(float) != audioBytes.Length)
                throw new ArgumentException(@"Video and audio must match the configured export format.");
            try
            {
                // Slots are used in submission order; the loop below always frees the one used next.
                int slot = (int)(WrittenFrames % readback_depth);

                var list = commands[slot]!;
                list.Begin();
                // Framebuffer textures have mip levels; the whole-texture overload requires matching levels.
                list.CopyTexture(texture, 0, 0, 0, 0, 0, staging[slot]!, 0, 0, 0, 0, 0, (uint)options.Width, (uint)options.Height, 1, 1);
                list.End();
                device.SubmitCommands(list, fences[slot]!);
                pendingReadbacks.Enqueue(slot);

                // The source texture is reused by the next frame, which the GPU orders after this copy.
                while (pendingReadbacks.Count >= readback_depth)
                    readBack(pendingReadbacks.Dequeue());

                Buffer.BlockCopy(audio, 0, audioBytes, 0, audioBytes.Length);
                audioFile!.Write(audioBytes);
                WrittenFrames++;
            }
            catch
            {
                faulted = true;
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        public void Complete()
        {
            checkThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed) return;
            if (faulted || WrittenFrames == 0)
                throw new InvalidOperationException(@"Cannot finalise an empty or failed export.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                while (pendingReadbacks.Count > 0)
                    readBack(pendingReadbacks.Dequeue());
                filledBuffers.CompleteAdding();
                if (!pipeWriter!.Join(pipe_timeout))
                    throw new TimeoutException(@"FFmpeg stopped accepting video frames.");
                throwIfPipeFailed();
                encoder!.StandardInput.Close();
                waitForExit(encoder, stderrTail!);
                cancellationRegistration.Dispose();
                encoder.Dispose();
                encoder = null;
                audioFile!.Dispose();
                audioFile = null;
                var info = createStartInfo();
                add(info, @"-i", videoPath, @"-f", @"f32le", @"-ar", @"48000", @"-ac", @"2", @"-i", audioPath,
                    @"-map", @"0:v:0", @"-map", @"1:a:0", @"-c:v", @"copy", @"-c:a", @"aac", @"-b:a", @"192k",
                    @"-movflags", @"+faststart", destination);
                using var muxer = Process.Start(info) ?? throw new InvalidOperationException(@"Unable to start FFmpeg muxing.");
                muxer.StandardInput.Close();
                var muxerTail = readTailAsync(muxer.StandardError);
                using var registration = cancellationToken.Register(() => kill(muxer));
                waitForExit(muxer, muxerTail);
                cancellationToken.ThrowIfCancellationRequested();
                completed = true;
            }
            catch
            {
                faulted = true;
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        /// <summary>
        /// Waits for a submitted copy and hands its pixels to the pipe writer.
        /// </summary>
        private void readBack(int slot)
        {
            if (!device.WaitForFence(fences[slot]!, 10000000000UL))
                throw new TimeoutException(@"GPU readback timed out.");
            device.ResetFence(fences[slot]!);

            byte[] pixels = takeFreeBuffer();
            var mapped = device.Map(staging[slot]!, MapMode.Read);
            try
            {
                int rowBytes = options.Width * 4;
                if (device.IsUvOriginTopLeft && mapped.RowPitch == rowBytes)
                    Marshal.Copy(mapped.Data, pixels, 0, pixels.Length);
                else
                {
                    for (int row = 0; row < options.Height; row++)
                    {
                        int sourceRow = device.IsUvOriginTopLeft ? row : options.Height - 1 - row;
                        Marshal.Copy(IntPtr.Add(mapped.Data, checked(sourceRow * (int)mapped.RowPitch)), pixels, row * rowBytes, rowBytes);
                    }
                }
            }
            catch
            {
                freeBuffers.Add(pixels);
                throw;
            }
            finally { device.Unmap(staging[slot]!); }

            filledBuffers.Add(pixels);
        }

        private byte[] takeFreeBuffer()
        {
            var deadline = Stopwatch.StartNew();

            while (true)
            {
                throwIfPipeFailed();
                cancellationToken.ThrowIfCancellationRequested();
                if (freeBuffers.TryTake(out byte[]? buffer, 100))
                    return buffer;
                if (deadline.Elapsed > pipe_timeout)
                {
                    kill(encoder);
                    throw new TimeoutException(@"FFmpeg stopped accepting video frames.");
                }
            }
        }

        private void writePipe(Stream input)
        {
            try
            {
                foreach (byte[] buffer in filledBuffers.GetConsumingEnumerable())
                {
                    input.Write(buffer);
                    freeBuffers.Add(buffer);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref pipeFailure, ex);
            }
        }

        private void throwIfPipeFailed()
        {
            if (Volatile.Read(ref pipeFailure) == null)
                return;

            cancellationToken.ThrowIfCancellationRequested();

            // A closed pipe usually means FFmpeg exited; report its own error rather than the pipe's.
            string detail = string.Empty;
            if (encoder != null && encoder.WaitForExit(5000) && encoder.ExitCode != 0)
                detail = describeExit(encoder, stderrTail!);
            throw new InvalidOperationException(string.IsNullOrEmpty(detail) ? @"FFmpeg stopped accepting video frames." : detail, pipeFailure);
        }

        private ProcessStartInfo createStartInfo()
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            add(info, @"-nostdin", @"-hide_banner", @"-loglevel", @"error", @"-n");
            return info;
        }

        private void waitForExit(Process process, Task<string> tail)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                tail.WaitAsync(timeout.Token).GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(describeExit(process, tail));
            }
            catch
            {
                kill(process);
                process.WaitForExit(5000);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        private static string describeExit(Process process, Task<string> tail)
        {
            // Arguments hold only local paths and encoder settings, so FFmpeg's own error is safe to show.
            string output = tail.Wait(1000) ? tail.GetResultSafely() : string.Empty;
            string message = $@"FFmpeg exited with code {process.ExitCode}. Check libx264/AAC support, encoder settings and available disk space.";
            return string.IsNullOrEmpty(output) ? message : $@"{message} {output}";
        }

        /// <summary>
        /// Drains standard error, keeping only its end so a verbose FFmpeg cannot grow memory.
        /// </summary>
        private static async Task<string> readTailAsync(StreamReader reader)
        {
            var tail = new StringBuilder();
            char[] buffer = new char[1024];
            int read;

            while ((read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > stderr_tail_length)
                    tail.Remove(0, tail.Length - stderr_tail_length);
            }

            return tail.ToString().Trim();
        }

        private static void add(ProcessStartInfo info, params string[] arguments)
        {
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
        }

        private static string number(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static void kill(Process? process)
        {
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            checkThread();
            if (disposed) return;
            disposed = true;
            cancellationRegistration.Dispose();
            filledBuffers.CompleteAdding();
            if (encoder != null)
            {
                // Killing FFmpeg breaks the pipe, which ends a writer blocked on it.
                kill(encoder);
                encoder.WaitForExit(5000);
                pipeWriter?.Join(5000);
                encoder.Dispose();
                encoder = null;
            }
            try
            {
                audioFile?.Dispose();
                device.WaitForIdle();
            }
            finally
            {
                for (int i = 0; i < readback_depth; i++)
                {
                    fences[i]?.Dispose();
                    commands[i]?.Dispose();
                    staging[i]?.Dispose();
                }
                try { File.Delete(videoPath); }
                finally
                {
                    try { File.Delete(audioPath); }
                    finally { if (!completed) File.Delete(destination); }
                }
            }
        }

        private void checkThread()
        {
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException(@"FFmpeg export must remain on its creating worker thread.");
        }
    }
}
