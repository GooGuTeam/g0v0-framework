// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class MipmapRegionsTest
    {
        private readonly List<RectangleI> regions = new List<RectangleI>();

        [Test]
        public void TestUploadIsAlignedOutwards()
        {
            Assert.That(plan(256, 256, new RectangleI(13, 17, 20, 9)), Is.True);
            Assert.That(regions, Is.EqualTo(new[] { new RectangleI(8, 16, 32, 16) }));
        }

        [Test]
        public void TestGlyphAndPaddingStripsMerge()
        {
            // A glyph and its padding strips, as uploaded by an atlas.
            Assert.That(plan(1024, 1024,
                new RectangleI(112, 112, 30, 40),
                new RectangleI(100, 112, 12, 40),
                new RectangleI(142, 112, 12, 40),
                new RectangleI(112, 100, 30, 12),
                new RectangleI(112, 152, 30, 12),
                new RectangleI(100, 100, 12, 12)), Is.True);

            Assert.That(regions, Is.EqualTo(new[] { new RectangleI(96, 96, 64, 72) }));
        }

        [Test]
        public void TestDistantUploadsStaySeparate()
        {
            Assert.That(plan(1024, 1024, new RectangleI(10, 10, 4, 4), new RectangleI(500, 500, 4, 4)), Is.True);
            Assert.That(regions, Is.EquivalentTo(new[] { new RectangleI(8, 8, 8, 8), new RectangleI(496, 496, 8, 8) }));
        }

        [Test]
        public void TestLargeUploadUsesWholeTexture()
        {
            Assert.That(plan(256, 256, new RectangleI(0, 0, 200, 100)), Is.False);
            Assert.That(regions, Is.Empty);
        }

        [Test]
        public void TestUnalignedTextureUsesWholeTexture()
            => Assert.That(plan(250, 256, new RectangleI(0, 0, 8, 8)), Is.False);

        [Test]
        public void TestUploadOutsideTextureUsesWholeTexture()
            => Assert.That(plan(256, 256, new RectangleI(250, 0, 10, 8)), Is.False);

        [Test]
        public void TestEdgeUploadIsClampedByAlignment()
        {
            Assert.That(plan(256, 256, new RectangleI(250, 250, 6, 6)), Is.True);
            Assert.That(regions, Is.EqualTo(new[] { new RectangleI(248, 248, 8, 8) }));
        }

        [Test]
        public void TestTooManyRegionsUseWholeTexture()
        {
            var uploads = new List<RectangleI>();

            for (int i = 0; i < 20; i++)
                uploads.Add(new RectangleI(i * 48, 0, 4, 4));

            Assert.That(plan(1024, 1024, uploads.ToArray()), Is.False);
        }

        [Test]
        public void TestDisabled()
        {
            MipmapRegions.Enabled = false;

            try
            {
                Assert.That(plan(256, 256, new RectangleI(8, 8, 8, 8)), Is.False);
            }
            finally
            {
                MipmapRegions.Enabled = true;
            }
        }

        private bool plan(int width, int height, params RectangleI[] uploads)
            => MipmapRegions.TryPlan(new List<RectangleI>(uploads), width, height, regions);
    }
}
