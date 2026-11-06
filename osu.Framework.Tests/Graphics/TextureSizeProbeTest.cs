// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.OpenGL;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Testing;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class TextureSizeProbeTest
    {
        [Test]
        public void TestAllSizesPassTrustsReportedSize()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            var renderer = createRenderer(storage, 16384, singleChannel: true);
            var attempts = new List<(int, TexturePixelFormat)>();

            renderer.ProbeTextureSizes("gpu", (size, format) =>
            {
                attempts.Add((size, format));
                return true;
            });

            Assert.That(attempts, Is.EqualTo(new[] { (2048, TexturePixelFormat.R8G8B8A8Float), (4096, TexturePixelFormat.R8G8B8A8Float), (8192, TexturePixelFormat.R8G8B8A8Float) }));
            Assert.That(renderer.MaxTextureSize, Is.EqualTo(16384));
            Assert.That(renderer.FontAtlasSize, Is.EqualTo(8192));
        }

        [Test]
        public void TestLargeRgbaFailureLimitsSize()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            var renderer = createRenderer(storage, 16384, singleChannel: true);

            // RGBA 8192 (256 MiB) fails, single-channel 8192 (64 MiB) fits.
            renderer.ProbeTextureSizes("gpu", (size, format) => size < 8192 || format == TexturePixelFormat.R8Float);

            Assert.That(renderer.MaxTextureSize, Is.EqualTo(4096));
            Assert.That(renderer.FontAtlasSize, Is.EqualTo(8192));
            Assert.That(renderer.TextureSizeSupport!.CoversReportedSize, Is.False);
        }

        [Test]
        public void TestRgbaFontAtlasKeepsDefaultSize()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            var renderer = createRenderer(storage, 16384, singleChannel: false);

            renderer.ProbeTextureSizes("gpu", (_, _) => true);

            Assert.That(renderer.FontAtlasSize, Is.EqualTo(4096));
        }

        [Test]
        public void TestSmallReportedSizeIsNotExceeded()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            var renderer = createRenderer(storage, 4096, singleChannel: true);
            var attempts = new List<int>();

            renderer.ProbeTextureSizes("gpu", (size, _) =>
            {
                attempts.Add(size);
                return true;
            });

            Assert.That(attempts.Max(), Is.EqualTo(4096));
            Assert.That(renderer.MaxTextureSize, Is.EqualTo(4096));
            Assert.That(renderer.FontAtlasSize, Is.EqualTo(4096));
        }

        [Test]
        public void TestResultIsCachedPerDevice()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            int calls = 0;

            bool allocate(int size, TexturePixelFormat format)
            {
                calls++;
                return size < 8192;
            }

            createRenderer(storage, 16384, true).ProbeTextureSizes("gpu", allocate);
            int firstLaunchCalls = calls;

            var second = createRenderer(storage, 16384, true);
            second.ProbeTextureSizes("gpu", allocate);

            Assert.That(calls, Is.EqualTo(firstLaunchCalls));
            Assert.That(second.MaxTextureSize, Is.EqualTo(4096));

            createRenderer(storage, 16384, true).ProbeTextureSizes("other-gpu", allocate);
            Assert.That(calls, Is.GreaterThan(firstLaunchCalls));
        }

        [Test]
        public void TestInterruptedProbeUsesConservativeSizes()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");

            // A probe that crashes the process leaves its marker behind and no result.
            createRenderer(storage, 16384, true).ProbeTextureSizes("gpu", (_, _) => true);
            string result = storage.GetFiles(string.Empty, "texture-size-*.json").Single();
            storage.Delete(result);
            using (var marker = storage.CreateFileSafely(Path.ChangeExtension(result, "pending")))
                marker.WriteByte(1);

            var renderer = createRenderer(storage, 16384, true);
            bool called = false;
            renderer.ProbeTextureSizes("gpu", (_, _) => called = true);

            Assert.That(called, Is.False);
            Assert.That(renderer.MaxTextureSize, Is.EqualTo(4096));
            Assert.That(renderer.FontAtlasSize, Is.EqualTo(4096));
        }

        [Test]
        public void TestWithoutCacheNothingChanges()
        {
            var renderer = new GLRenderer { SingleChannelFontAtlas = true };
            bool called = false;

            renderer.ProbeTextureSizes("gpu", (_, _) => called = true);

            Assert.That(called, Is.False);
            Assert.That(renderer.FontAtlasSize, Is.EqualTo(4096));
        }

        private static Renderer createRenderer(TemporaryNativeStorage storage, int reported, bool singleChannel)
        {
            var renderer = new GLRenderer
            {
                GraphicsProbeCache = new GraphicsProbeCache(storage),
                SingleChannelFontAtlas = singleChannel,
            };

            // Set by renderer initialisation from the driver.
            typeof(Renderer).GetProperty(nameof(Renderer.MaxTextureSize))!.SetValue(renderer, reported);
            return renderer;
        }
    }
}
