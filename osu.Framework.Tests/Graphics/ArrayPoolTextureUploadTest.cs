// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class ArrayPoolTextureUploadTest
    {
        [TestCase(3, 5)]
        [TestCase(0, 5)]
        public void TestLogicalDataLength(int width, int height)
        {
            var pool = new TrackingPool();
            using var upload = new ArrayPoolTextureUpload(width, height, pool);
            Assert.That(upload.RawData.Length, Is.EqualTo(width * height));
            Assert.That(upload.Data.Length, Is.EqualTo(width * height));
            Assert.That(((ITextureUpload)upload).RawData.Length, Is.EqualTo(width * height * 4));
            upload.RawData.Clear();
            Assert.That(pool.Buffer[width * height].R, Is.EqualTo(123));
        }

        [TestCase(-1, 2)]
        [TestCase(2, -1)]
        [TestCase(-1, -1)]
        public void TestNegativeDimensions(int width, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ArrayPoolTextureUpload(width, height));
        }

        [Test]
        public void TestDimensionOverflow()
        {
            Assert.Throws<OverflowException>(() => _ = new ArrayPoolTextureUpload(65536, 65536));
        }

        [Test]
        public void TestDisposeReturnsArrayOnlyOnce()
        {
            var pool = new TrackingPool();
            var upload = new ArrayPoolTextureUpload(2, 3, pool);
            // Parallel.For completes all disposals before returning.
            // ReSharper disable once AccessToDisposedClosure
            Parallel.For(0, 100, _ => upload.Dispose());
            upload.Dispose();
            Assert.That(pool.ReturnCount, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => { _ = upload.RawData.Length; });
            Assert.Throws<ObjectDisposedException>(() => { _ = upload.Data.Length; });
            Assert.Throws<ObjectDisposedException>(() => { _ = ((ITextureUpload)upload).RawData.Length; });
        }

        private sealed class TrackingPool : ArrayPool<Rgba32>
        {
            public Rgba32[] Buffer { get; private set; } = Array.Empty<Rgba32>();
            public int ReturnCount;

            public override Rgba32[] Rent(int minimumLength)
            {
                Buffer = new Rgba32[minimumLength + 16];
                Array.Fill(Buffer, new Rgba32(123, 0, 0, 255));
                return Buffer;
            }

            public override void Return(Rgba32[] array, bool clearArray = false)
            {
                Assert.That(array, Is.SameAs(Buffer));
                Interlocked.Increment(ref ReturnCount);
            }
        }
    }
}
