// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class AlphaTextureUploadTest
    {
        [Test]
        public void TestRgbaConversion()
        {
            byte[] coverage = { 0, 1, 127, 255, 99 };
            using var alpha = new AlphaTextureUpload(2, 2, coverage);
            alpha.Bounds = new RectangleI(7, 9, 2, 2);
            alpha.Level = 2;
            using var rgba = alpha.ToRgbaUpload();

            Assert.That(alpha.RawData.Length, Is.EqualTo(4));
            Assert.That(rgba.Format, Is.EqualTo(TexturePixelFormat.R8G8B8A8Float));
            Assert.That(rgba.Width, Is.EqualTo(2));
            Assert.That(rgba.Height, Is.EqualTo(2));
            Assert.That(rgba.Bounds, Is.EqualTo(alpha.Bounds));
            Assert.That(rgba.Level, Is.EqualTo(alpha.Level));
            Assert.That(rgba.Data.Length, Is.EqualTo(4));
            for (int i = 0; i < rgba.Data.Length; i++)
                Assert.That(rgba.Data[i], Is.EqualTo(new Rgba32(255, 255, 255, coverage[i])));
        }

        [TestCase(-1, -1)]
        [TestCase(0, 1)]
        [TestCase(65536, 65536)]
        public void TestInvalidDimensions(int width, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AlphaTextureUpload(width, height, new byte[1]));
        }

        [Test]
        public void TestInsufficientCoverage()
        {
            Assert.Throws<ArgumentException>(() => _ = new AlphaTextureUpload(2, 2, new byte[3]));
        }
    }
}
