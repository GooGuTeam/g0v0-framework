// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class AtlasPackerTest
    {
        [TestCase(AtlasPackingStrategy.Etagere)]
        [TestCase(AtlasPackingStrategy.Guillotiere)]
        [TestCase(AtlasPackingStrategy.FreeRectangles)]
        public void TestBoundsPaddingAndNonOverlap(AtlasPackingStrategy strategy)
        {
            IAtlasPacker packer = create(strategy);
            var random = new Random(731);
            for (int page = 0; page < 8; page++)
            {
                packer.Reset(1024, 512);
                var occupied = new List<RectangleI>();
                for (int i = 0; i < 1000; i++)
                {
                    int width = random.Next(0, 150);
                    int height = random.Next(0, 100);
                    if (!packer.TryAllocate(width, height, out var position))
                        continue;
                    var footprint = new RectangleI(position.X, position.Y, width + TextureAtlas.PADDING, height + TextureAtlas.PADDING);
                    Assert.That(footprint.Left, Is.GreaterThanOrEqualTo(TextureAtlas.PADDING));
                    Assert.That(footprint.Top, Is.GreaterThanOrEqualTo(TextureAtlas.PADDING));
                    Assert.That(footprint.Right, Is.LessThanOrEqualTo(1024));
                    Assert.That(footprint.Bottom, Is.LessThanOrEqualTo(512));
                    Assert.That(footprint.IntersectsWith(new RectangleI(0, 0, TextureAtlas.PADDING + 1, TextureAtlas.PADDING + 1)), Is.False);
                    foreach (var other in occupied)
                        Assert.That(footprint.IntersectsWith(other), Is.False, $"Overlap: {footprint}, {other}");
                    occupied.Add(footprint);
                }
                Assert.That(occupied.Count, Is.GreaterThan(10));
            }
        }

        [TestCase(AtlasPackingStrategy.Etagere)]
        [TestCase(AtlasPackingStrategy.Guillotiere)]
        public void TestEdgeSizesAndPageRollover(AtlasPackingStrategy strategy)
        {
            const int size = 128;
            int limit = size - 2 * TextureAtlas.PADDING;
            int[] dimensions = { 0, 1, limit - 1, limit, limit + 1, int.MaxValue };
            foreach (int width in dimensions)
                foreach (int height in dimensions)
                {
                    var atlas = new TextureAtlas(new DummyRenderer(), size, size, strategy);
                    bool fits = width <= limit && height <= limit && (width < limit || height < limit);
                    var first = atlas.Add(width, height);
                    Assert.That(first != null, Is.EqualTo(fits), $"{width}x{height}");
                    if (first == null)
                        continue;
                    var rect = first.GetTextureRect();
                    for (int i = 0; i < 10; i++)
                        Assert.That(atlas.Add(limit, limit - 1), Is.Not.Null);
                    Assert.That(first.GetTextureRect(), Is.EqualTo(rect), "A rollover must not relocate existing regions.");
                }
            Assert.Throws<ArgumentOutOfRangeException>(() => new TextureAtlas(new DummyRenderer(), size, size, strategy).Add(-1, 10));
        }

        [TestCase(AtlasPackingStrategy.FreeRectangles, false)]
        [TestCase(AtlasPackingStrategy.Etagere, false)]
        [TestCase(AtlasPackingStrategy.Guillotiere, false)]
        [TestCase(AtlasPackingStrategy.FreeRectangles, true)]
        [TestCase(AtlasPackingStrategy.Etagere, true)]
        [TestCase(AtlasPackingStrategy.Guillotiere, true)]
        public void TestPackingWorkload(AtlasPackingStrategy strategy, bool varied)
        {
            var sizes = new Vector2I[4000];
            var random = new Random(148);
            for (int i = 0; i < sizes.Length; i++)
                sizes[i] = varied ? new Vector2I(random.Next(16, 513), random.Next(16, 513)) : new Vector2I(random.Next(16, 80), random.Next(40, 64));

            IAtlasPacker packer = create(strategy);
            int pages = 0;
            double[] measurements = new double[5];
            for (int iteration = -1; iteration < measurements.Length; iteration++)
            {
                packer.Reset(4096, 4096);
                pages = 1;
                var timer = Stopwatch.StartNew();
                foreach (var size in sizes)
                {
                    if (packer.TryAllocate(size.X, size.Y, out _))
                        continue;
                    pages++;
                    packer.Reset(4096, 4096);
                    Assert.That(packer.TryAllocate(size.X, size.Y, out _), Is.True);
                }
                timer.Stop();
                if (iteration >= 0)
                    measurements[iteration] = timer.Elapsed.TotalMilliseconds;
            }
            Array.Sort(measurements);
            TestContext.Out.WriteLine($"{strategy}, varied={varied}: {sizes.Length} allocations, {pages} pages, {measurements[2]:F3} ms (median of 5 after warmup; synthetic CPU-only smoke measurement)");
        }

        private static IAtlasPacker create(AtlasPackingStrategy strategy) => strategy switch
        {
            AtlasPackingStrategy.Etagere => new EtagereAtlasPacker(),
            AtlasPackingStrategy.Guillotiere => new GuillotiereAtlasPacker(),
            _ => new TextureAtlas.FreeRectanglePacker(),
        };
    }
}
