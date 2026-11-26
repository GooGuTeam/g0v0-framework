// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using osu.Framework.Configuration;
using osu.Framework.Development;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.Graphics.Video;
using osu.Framework.IO.Stores;
using osuTK;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Media Foundation decodes the first frame of each test video to the same YUV planes as FFmpeg, read back from the GPU,
    /// in software and with DXVA. FLV is left out: Media Foundation cannot open it, and beatmaps may not use it.
    /// </summary>
    [TestFixture]
    public class MediaFoundationVideoDecoderGpuTest
    {
        private static readonly string[] videos = { "h264.mp4", "h264-hd.mp4", "h264.mov", "h264.mkv", "h264.avi", "hevc.mp4", "vp8.webm", "vp9.webm" };

        private static readonly HardwareVideoDecoder[] modes = { HardwareVideoDecoder.None, HardwareVideoDecoder.Any };

        private static readonly PropertyInfo zero_copy_flag = typeof(FrameworkFeatureFlags).GetProperty(nameof(FrameworkFeatureFlags.ZeroCopyVideoDecoding))!;

        private VeldridRenderer renderer = null!;
        private bool wasDrawThread;
        private bool wasZeroCopy;

        [SetUp]
        public void SetUp()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Ignore("Media Foundation is Windows-only.");

            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            // Zero-copy decoding is on by default; tests other than the zero-copy ones cover reading frames back.
            wasZeroCopy = FrameworkFeatureFlags.ZeroCopyVideoDecoding;
            zero_copy_flag.SetValue(null, false);

            renderer = new VeldridRenderer();
            renderer.InitialiseOffline(64, 64);
        }

        [TearDown]
        public void TearDown()
        {
            if (renderer.IsInitialised)
                renderer.DisposeOffline();
            ThreadSafety.IsDrawThread = wasDrawThread;
            zero_copy_flag.SetValue(null, wasZeroCopy);
        }

        [Test]
        [Explicit("Requires an offline graphics device, GPU readback and the native GPU video library.")]
        public void TestFirstFrameMatchesFfmpeg([ValueSource(nameof(videos))] string name, [ValueSource(nameof(modes))] HardwareVideoDecoder mode)
        {
            using var mediaFoundation = createDecoder(name, mode);
            var (mfTime, mfPlanes) = decodeFirstFrame(mediaFoundation);

            using var ffmpeg = new FFmpegVideoDecoder(renderer, openVideo(name));
            var (ffTime, ffPlanes) = decodeFirstFrame(ffmpeg);

            TestContext.Progress.WriteLine($"{name}: {mediaFoundation.FrameRate:0.##} fps, duration {mediaFoundation.Duration:0} ms (FFmpeg {ffmpeg.Duration:0} ms), "
                                           + $"first frame {mfTime:0.#} ms (FFmpeg {ffTime:0.#} ms), {describeDifference(mfPlanes, ffPlanes)}");

            Assert.That(mfPlanes.Select(p => p.Length), Is.EqualTo(ffPlanes.Select(p => p.Length)), "plane sizes");
            Assert.That(mfTime, Is.EqualTo(ffTime).Within(1000 / 24.0), "first frame time");

            for (int i = 0; i < 3; i++)
            {
                // Decoders may round differently; anything beyond a few levels on average means wrong planes or layout.
                double mean = mfPlanes[i].Zip(ffPlanes[i], (a, b) => (double)Math.Abs(a - b)).Average();
                Assert.That(mean, Is.LessThan(2), $"mean difference in plane {i}");
            }
        }

        [Test]
        [Explicit("Requires an offline graphics device and the native GPU video library.")]
        public void TestSeekAndEndOfStream([ValueSource(nameof(modes))] HardwareVideoDecoder mode)
        {
            using var decoder = createDecoder("h264.mp4", mode);
            decoder.StartDecoding();
            Assert.That(decoder.State, Is.Not.EqualTo(VideoDecoder.DecoderState.Faulted));

            double target = decoder.Duration / 2;
            decoder.Seek(target);
            var frame = waitForFrame(decoder, f => f.Time >= target - 1);
            TestContext.Progress.WriteLine($"seek to {target:0} ms gave {frame.Time:0.#} ms");
            Assert.That(frame.Time, Is.EqualTo(target).Within(100));

            var stopwatch = Stopwatch.StartNew();

            while (decoder.State != VideoDecoder.DecoderState.EndOfStream)
            {
                Assert.That(stopwatch.Elapsed.TotalSeconds, Is.LessThan(30), "reaching the end");
                Assert.That(decoder.IsFaulted, Is.False);
                decoder.ReturnFrames(decoder.GetDecodedFrames());
                Thread.Sleep(1);
            }
        }

        /// <summary>
        /// Zero-copy decoding produces the same planes as reading DXVA frames back, on both renderers that support it.
        /// </summary>
        [Test]
        [Explicit("Requires an offline graphics device, GPU readback and the native GPU video library.")]
        public void TestZeroCopyMatchesReadback([Values("h264.mp4", "h264-hd.mp4", "hevc.mp4", "vp9.webm")] string name, [Values(false, true)] bool vulkan)
        {
            if (vulkan)
                useVulkanRenderer();

            double readbackTime;
            byte[][] readbackPlanes;

            using (var readback = createDecoder(name, HardwareVideoDecoder.Any))
            {
                (readbackTime, readbackPlanes) = decodeFirstFrame(readback);
                Assert.That(readback.UsesSharedFrames, Is.False);
            }

            zero_copy_flag.SetValue(null, true);

            double sharedTime;
            byte[][] sharedPlanes;

            using (var zeroCopy = createDecoder(name, HardwareVideoDecoder.Any))
            {
                (sharedTime, sharedPlanes) = decodeFirstFrame(zeroCopy);
                Assert.That(zeroCopy.UsesSharedFrames, Is.True, "zero-copy decoding in use");
            }

            TestContext.Progress.WriteLine($"{name} ({renderer.Device.BackendType}): {describeDifference(sharedPlanes, readbackPlanes)}");
            Assert.That(sharedTime, Is.EqualTo(readbackTime));
            Assert.That(sharedPlanes, Is.EqualTo(readbackPlanes));
        }

        /// <summary>
        /// When the renderer cannot take shared frames, decoding carries on by reading frames back instead of stopping.
        /// </summary>
        [Test]
        [Explicit("Requires an offline graphics device, GPU readback and the native GPU video library.")]
        public void TestZeroCopyFailureFallsBackToReadback([Values(false, true)] bool vulkan)
        {
            if (vulkan)
                useVulkanRenderer();

            zero_copy_flag.SetValue(null, true);

            using var decoder = createDecoder("h264.mp4", HardwareVideoDecoder.Any);
            decodeFirstFrame(decoder);
            Assert.That(decoder.UsesSharedFrames, Is.True, "zero-copy decoding in use");

            typeof(MediaFoundationVideoDecoder).GetMethod("giveUpSharedFrames", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(decoder, new object[] { "test" });
            Assert.That(decoder.UsesSharedFrames, Is.False);

            // Up to seven shared frames (queued frames, slots and a kept-back one) can predate the switch; they are dropped on upload.
            int skipped = 0;
            var frame = waitForFrame(decoder, _ => ++skipped > 8);
            Assert.That(readPlanes(frame)[0].Any(b => b != 0), Is.True, "read-back frame has data");
            Assert.That(decoder.IsFaulted, Is.False);
        }

        private void useVulkanRenderer()
        {
            renderer.DisposeOffline();

            var offlineVulkan = typeof(FrameworkFeatureFlags).GetProperty(nameof(FrameworkFeatureFlags.OfflineVulkanRenderer))!;
            object? wasOfflineVulkan = offlineVulkan.GetValue(null);
            offlineVulkan.SetValue(null, true);

            try
            {
                renderer = new VeldridRenderer();
                renderer.InitialiseOffline(64, 64);
            }
            finally
            {
                offlineVulkan.SetValue(null, wasOfflineVulkan);
            }

            Assert.That(renderer.Device.BackendType, Is.EqualTo(GraphicsBackend.Vulkan));
            if (!renderer.Device.GetVulkanInfo().Win32CaptureInteropEnabled)
                Assert.Ignore("The Vulkan device lacks Win32 capture interop.");
        }

        /// <summary>
        /// Decodes a whole video without uploading frames, reporting the process CPU time spent, to compare software decoding with DXVA.
        /// </summary>
        [Test]
        [Explicit("Requires an offline graphics device and the native GPU video library; prints timings.")]
        public void TestDecodingCost([Values("h264-hd.mp4", "hevc.mp4")] string name, [ValueSource(nameof(modes))] HardwareVideoDecoder mode)
        {
            using var decoder = createDecoder(name, mode);
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            int frames = 0;

            decoder.StartDecoding();

            while (decoder.State != VideoDecoder.DecoderState.EndOfStream)
            {
                Assert.That(decoder.IsFaulted, Is.False);
                Assert.That(stopwatch.Elapsed.TotalSeconds, Is.LessThan(60), "reaching the end");

                var decoded = decoder.GetDecodedFrames().ToArray();
                frames += decoded.Length;
                decoder.ReturnFrames(decoded);
                Thread.Sleep(1);
            }

            process.Refresh();
            double cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            TestContext.Progress.WriteLine($"{name} ({mode}): {frames} frames in {stopwatch.ElapsedMilliseconds} ms, "
                                           + $"{cpu:0} ms CPU ({cpu / Math.Max(frames, 1):0.00} ms per frame)");
        }

        private MediaFoundationVideoDecoder createDecoder(string name, HardwareVideoDecoder mode)
        {
            var decoder = new MediaFoundationVideoDecoder(renderer, openVideo(name));
            decoder.TargetHardwareVideoDecoders.Value = mode;
            return decoder;
        }

        private (double time, byte[][] planes) decodeFirstFrame(VideoDecoder decoder)
        {
            decoder.StartDecoding();
            var frame = waitForFrame(decoder, _ => true);
            return (frame.Time, readPlanes(frame));
        }

        private static DecodedFrame waitForFrame(VideoDecoder decoder, Func<DecodedFrame, bool> accept)
        {
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                Assert.That(decoder.IsFaulted, Is.False, "decoder faulted");
                Assert.That(stopwatch.Elapsed.TotalSeconds, Is.LessThan(10), "waiting for a frame");

                foreach (var frame in decoder.GetDecodedFrames())
                {
                    if (accept(frame))
                        return frame;

                    decoder.ReturnFrames(new[] { frame });
                }

                Thread.Sleep(1);
            }
        }

        private byte[][] readPlanes(DecodedFrame frame)
        {
            var texture = (VeldridVideoTexture)frame.Texture.NativeTexture;

            // Uploads are recorded into the frame's command lists, which only run once the frame finishes.
            renderer.BeginFrame(new Vector2(64, 64));
            texture.Upload();
            renderer.FinishFrame();
            renderer.Device.WaitForIdle();

            byte[][] planes = texture.GetResourceList().Select(resources =>
            {
                var source = resources.Texture!;
                using var staging = renderer.Factory.CreateTexture(TextureDescription.Texture2D(source.Width, source.Height, 1, 1, source.Format, TextureUsage.Staging));
                using var commands = renderer.Factory.CreateCommandList();
                commands.Begin();
                commands.CopyTexture(source, 0, 0, 0, 0, 0, staging, 0, 0, 0, 0, 0, source.Width, source.Height, 1, 1);
                commands.End();
                renderer.Device.SubmitCommands(commands);
                renderer.Device.WaitForIdle();

                var mapped = renderer.Device.Map(staging, MapMode.Read);
                byte[] data = new byte[source.Width * source.Height];
                for (int row = 0; row < source.Height; row++)
                    Marshal.Copy(mapped.Data + (int)(row * mapped.RowPitch), data, (int)(row * source.Width), (int)source.Width);
                renderer.Device.Unmap(staging);
                return data;
            }).ToArray();

            Assert.That(planes[0].Any(b => b != 0), Is.True, "the frame was read back");
            return planes;
        }

        private static string describeDifference(byte[][] actual, byte[][] expected)
        {
            if (actual.Select(p => p.Length).SequenceEqual(expected.Select(p => p.Length)) == false)
                return "plane sizes differ";

            return string.Join(", ", Enumerable.Range(0, 3).Select(i =>
            {
                int[] differences = actual[i].Zip(expected[i], (a, b) => Math.Abs(a - b)).ToArray();
                return $"plane {i} mean {differences.Average():0.###} max {differences.Max()}";
            }));
        }

        private static Stream openVideo(string name)
        {
            var store = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(MediaFoundationVideoDecoderGpuTest).Assembly), "Resources/Videos");
            return new MemoryStream(store.Get(name) ?? throw new FileNotFoundException(name));
        }
    }
}
