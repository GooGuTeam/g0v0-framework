// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Audio.Mixing;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Video;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Testing;
using osu.Framework.Threading;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    [NonParallelizable]
    public class VideoRecordingFailureTest
    {
        [Test]
        [Platform("Win")]
        public void TestUnsupportedCaptureFaultsStartupTask()
        {
            using var host = new HeadlessGameHost();
            using var mixer = new OfflineAudioMixer();
            using var audio = mixer.CreateAudioManager(new ResourceStore<byte[]>());
            var drawThread = new DrawThread(() => { }, host);
            typeof(GameHost).GetProperty(nameof(GameHost.DrawThread))!.SetValue(host, drawThread);
            typeof(GameHost).GetProperty(nameof(GameHost.Renderer))!.SetValue(host, new DummyRenderer());

            Task<DesktopVideoRecording> startup = DesktopVideoRecording.StartStreamingAsync(host, audio, _ => { });

            Assert.That(() => drawThread.Scheduler.Update(), Throws.Nothing);
            Assert.ThrowsAsync<NotSupportedException>(async () => await startup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        }

        [Test]
        [Platform("Win")]
        [Explicit("Requires a graphics device for the offline recorder.")]
        public void TestCancelledExportPreservesDestinationCreatedAfterStartup()
        {
            using var storage = new TemporaryNativeStorage(Guid.NewGuid().ToString());
            using var context = new OfflineRenderContext(32, 32);
            using var cancellation = new CancellationTokenSource();
            string path = storage.GetFullPath("output.mp4");
            // An executable stub is sufficient because the test cancels before encoding any frames.
            using var recording = new FfmpegOfflineVideoRecording(context, path, new VideoRecordingOptions(width: 32, height: 32),
                Path.Combine(Environment.SystemDirectory, "where.exe"), cancellation.Token);

            File.WriteAllText(path, "other export");
            cancellation.Cancel();
            recording.Dispose();

            Assert.That(File.ReadAllText(path), Is.EqualTo("other export"));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Platform("Win")]
        [Explicit("Requires a graphics device and FFmpeg with libx264 on PATH.")]
        public void TestPublishCompletedExport(bool destinationCreatedDuringExport)
        {
            using var storage = new TemporaryNativeStorage(Guid.NewGuid().ToString());
            using var context = new OfflineRenderContext(32, 32);
            string path = storage.GetFullPath("output.mp4");
            using var texture = context.Device.ResourceFactory.CreateTexture(
                TextureDescription.Texture2D(32, 32, 1, 1, PixelFormat.R8G8B8A8UNorm, TextureUsage.Sampled));
            context.Device.UpdateTexture(texture, new byte[32 * 32 * 4], 0, 0, 0, 32, 32, 1, 0, 0);

            using (var recording = new FfmpegOfflineVideoRecording(context, path, new VideoRecordingOptions(width: 32, height: 32), "ffmpeg", CancellationToken.None))
            {
                float[] audio = new float[recording.AudioFramesPerVideoFrame * 2];
                for (int i = 0; i < 4; i++)
                    recording.WriteFrame(texture, audio);

                if (destinationCreatedDuringExport)
                {
                    File.WriteAllText(path, "other export");
                    Assert.That(recording.Complete, Throws.TypeOf<IOException>());
                }
                else
                    recording.Complete();
            }

            Assert.That(Directory.GetFiles(storage.GetFullPath(".")), Is.EqualTo(new[] { path }));

            if (destinationCreatedDuringExport)
                Assert.That(File.ReadAllText(path), Is.EqualTo("other export"));
            else
                Assert.That(File.ReadAllBytes(path)[4..8], Is.EqualTo("ftyp"u8.ToArray()));
        }
    }
}
