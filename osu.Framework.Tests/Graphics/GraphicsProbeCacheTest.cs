// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Testing;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class GraphicsProbeCacheTest
    {
        [Test]
        public void TestReuseFingerprintChangeAndClear()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            var cache = new GraphicsProbeCache(storage);
            int calls = 0;
            BindlessTextureProbeResult probe()
            {
                calls++;
                return new BindlessTextureProbeResult(BindlessTextureProbeStatus.FeaturesReported, "test");
            }
            cache.GetOrProbe("gpu-driver-a", probe);
            var cached = cache.GetOrProbe("gpu-driver-a", probe);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(cached.RendererImplemented, Is.False);
            cache.GetOrProbe("gpu-driver-b", probe);
            Assert.That(calls, Is.EqualTo(2));
            using (var unrelated = storage.CreateFileSafely("unrelated"))
                unrelated.WriteByte(1);
            cache.Clear();
            Assert.That(storage.Exists("unrelated"), Is.True);
            cache.GetOrProbe("gpu-driver-a", probe);
            Assert.That(calls, Is.EqualTo(3));
        }

        [Test]
        public void TestCorruptAndInterruptedCache()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            var cache = new GraphicsProbeCache(storage);
            int calls = 0;
            BindlessTextureProbeResult probe()
            {
                calls++;
                return new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unsupported, "test");
            }
            cache.GetOrProbe("gpu", probe);
            string filename = storage.GetFiles(string.Empty, "*.json").Single();
            using (var writer = new StreamWriter(storage.CreateFileSafely(filename)))
                writer.Write("invalid json");
            cache.GetOrProbe("gpu", probe);
            Assert.That(calls, Is.EqualTo(2));
            storage.Delete(filename);
            using (var marker = storage.CreateFileSafely(Path.ChangeExtension(filename, "pending")))
                marker.WriteByte(1);
            var result = cache.GetOrProbe("gpu", probe);
            Assert.That(result.Status, Is.EqualTo(BindlessTextureProbeStatus.Interrupted));
            Assert.That(calls, Is.EqualTo(2));
            cache.Clear();
            cache.GetOrProbe("gpu", probe);
            Assert.That(calls, Is.EqualTo(3));
        }

        [Test]
        public void TestFailureIsCached()
        {
            using var storage = new TemporaryNativeStorage($"probe-{Guid.NewGuid()}");
            var cache = new GraphicsProbeCache(storage);
            var result = cache.GetOrProbe("gpu", () => throw new InvalidOperationException("driver failure"));
            Assert.That(result.Status, Is.EqualTo(BindlessTextureProbeStatus.Failed));
            Assert.That(cache.GetOrProbe("gpu", () => throw new AssertionException("Must not retry")), Is.EqualTo(result));
        }
    }
}
