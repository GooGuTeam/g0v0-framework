// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.Graphics.Video;
using osu.Framework.IO.Stores;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Media Foundation decodes the first frame of each test video to the same YUV planes as FFmpeg, read back from the GPU,
    /// or falls back to FFmpeg for formats it cannot open.
    /// </summary>
    [TestFixture]
    public class MediaFoundationVideoDecoderGpuTest
    {
        private static readonly string[] videos = { "h264.mp4", "h264-hd.mp4", "h264.mov", "h264.mkv", "h264.avi", "h264.flv", "hevc.mp4", "vp8.webm", "vp9.webm" };

        private VeldridRenderer renderer = null!;
        private bool wasDrawThread;

        [SetUp]
        public void SetUp()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Ignore("Media Foundation is Windows-only.");

            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            renderer = new VeldridRenderer();
            renderer.InitialiseOffline(64, 64);
        }

        [TearDown]
        public void TearDown()
        {
            if (renderer.IsInitialised)
                renderer.DisposeOffline();
            ThreadSafety.IsDrawThread = wasDrawThread;
        }

        [TestCaseSource(nameof(videos))]
        [Explicit("Requires an offline graphics device, GPU readback and the native GPU video library.")]
        public void TestFirstFrameMatchesFfmpeg(string name)
        {
            using var mediaFoundation = new MediaFoundationVideoDecoder(renderer, openVideo(name));
            var (mfTime, mfPlanes) = decodeFirstFrame(mediaFoundation);

            using var ffmpeg = new VideoDecoder(renderer, openVideo(name));
            var (ffTime, ffPlanes) = decodeFirstFrame(ffmpeg);

            string decoder = mediaFoundation.UsesMediaFoundation ? "Media Foundation" : "FFmpeg fallback";
            TestContext.Progress.WriteLine($"{name}: {decoder}, {mediaFoundation.FrameRate:0.##} fps, duration {mediaFoundation.Duration:0} ms (FFmpeg {ffmpeg.Duration:0} ms), "
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
        public void TestSeekAndEndOfStream()
        {
            using var decoder = new MediaFoundationVideoDecoder(renderer, openVideo("h264.mp4"));
            decoder.StartDecoding();
            Assert.That(decoder.UsesMediaFoundation, Is.True);

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
            texture.Upload();
            renderer.Device.WaitForIdle();

            return texture.GetResourceList().Select(resources =>
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
