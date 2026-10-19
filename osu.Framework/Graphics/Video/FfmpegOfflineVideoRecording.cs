// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Graphics.Rendering;
using Veldrid;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Worker-owned CPU-readback export using an installed FFmpeg with libx264.
    /// PCM is spooled to disk and muxed after video encoding to avoid two-input pipe deadlocks.
    /// No network destination, shell or unbounded frame queue is used.
    /// </summary>
    public sealed class FfmpegOfflineVideoRecording : IOfflineVideoRecording
    {
        private readonly GraphicsDevice device;
        private readonly VideoRecordingOptions options;
        private readonly string executable;
        private readonly string destination;
        private readonly string videoPath;
        private readonly string audioPath;
        private readonly CancellationToken cancellationToken;
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private readonly byte[] pixels;
        private readonly byte[] audioBytes;
        private Texture? staging;
        private CommandList? commands;
        private Fence? fence;
        private FileStream? audioFile;
        private Process? encoder;
        private Task? stderrDrain;
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
            pixels = new byte[checked(options.Width * options.Height * 4)];
            audioBytes = new byte[AudioFramesPerVideoFrame * 2 * sizeof(float)];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                staging = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D((uint)options.Width, (uint)options.Height,
                    1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Staging));
                commands = device.ResourceFactory.CreateCommandList();
                fence = device.ResourceFactory.CreateFence(false);
                audioFile = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
                var info = createStartInfo();
                add(info, @"-f", @"rawvideo", @"-pixel_format", @"rgba", @"-video_size", $@"{options.Width}x{options.Height}",
                    @"-framerate", number(options.FrameRate), @"-i", @"pipe:0", @"-an", @"-c:v", @"libx264", @"-preset", @"veryfast",
                    @"-b:v", number(options.Bitrate), @"-pix_fmt", @"yuv420p", @"-g", number(options.FrameRate * 2));
                if (options.H264Profile != 0)
                    add(info, @"-profile:v", options.H264Profile == 66 ? @"baseline" : options.H264Profile == 77 ? @"main" : @"high");
                add(info, videoPath);
                encoder = Process.Start(info) ?? throw new InvalidOperationException(@"Unable to start FFmpeg.");
                stderrDrain = encoder.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
                cancellationRegistration = cancellationToken.Register(() => kill(encoder));
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
                commands!.Begin();
                commands.CopyTexture(texture, staging!);
                commands.End();
                device.SubmitCommands(commands, fence!);
                if (!device.WaitForFence(fence!, 10000000000UL))
                    throw new TimeoutException(@"GPU readback timed out.");
                device.ResetFence(fence!);
                var mapped = device.Map(staging!, MapMode.Read);
                try
                {
                    int rowBytes = options.Width * 4;
                    for (int row = 0; row < options.Height; row++)
                    {
                        int sourceRow = device.IsUvOriginTopLeft ? row : options.Height - 1 - row;
                        Marshal.Copy(IntPtr.Add(mapped.Data, checked(sourceRow * (int)mapped.RowPitch)), pixels, row * rowBytes, rowBytes);
                    }
                }
                finally { device.Unmap(staging!); }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var interrupt = timeout.Token.Register(() => kill(encoder));
                encoder!.StandardInput.BaseStream.WriteAsync(pixels.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
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
                encoder!.StandardInput.Close();
                waitForExit(encoder, stderrDrain!);
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
                var drain = muxer.StandardError.BaseStream.CopyToAsync(Stream.Null);
                using var registration = cancellationToken.Register(() => kill(muxer));
                waitForExit(muxer, drain);
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

        private void waitForExit(Process process, Task drain)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                drain.WaitAsync(timeout.Token).GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($@"FFmpeg exited with code {process.ExitCode}. Check libx264/AAC support, encoder settings and available disk space.");
            }
            catch
            {
                kill(process);
                process.WaitForExit(5000);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
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
            if (encoder != null)
            {
                kill(encoder);
                encoder.WaitForExit(5000);
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
                fence?.Dispose();
                commands?.Dispose();
                staging?.Dispose();
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
