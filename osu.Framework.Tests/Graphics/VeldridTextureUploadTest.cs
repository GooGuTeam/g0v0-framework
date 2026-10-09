// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Graphics.Veldrid.Pipelines;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class VeldridTextureUploadTest
    {
        [TestCase(5, 256, 5)] // Driver-aligned staging row.
        [TestCase(16, 8, 5)] // Source padding exceeds destination padding.
        [TestCase(8, 64, 5)] // A pooled staging texture wider than the upload.
        [TestCase(-8, 64, 5)] // Bottom-up video frame.
        [TestCase(12, 256, 12)] // Multi-byte pixels.
        public void TestIndependentRowStrides(int sourceStride, int destinationStride, int rowSize)
        {
            const int height = 3;
            // Extra source padding makes an erroneous read deterministic rather than causing a native crash.
            byte[] source = Enumerable.Repeat((byte)0xcc, Math.Max(Math.Abs(sourceStride), destinationStride) * height).ToArray();
            byte[] destination = Enumerable.Repeat((byte)0xee, destinationStride * height).ToArray();
            int sourceStart = sourceStride < 0 ? Math.Abs(sourceStride) * (height - 1) : 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < rowSize; x++)
                    source[sourceStart + y * sourceStride + x] = (byte)(y * rowSize + x);
            }

            var sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
            var destinationHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);

            try
            {
                BasicPipeline.CopyTextureRows(sourceHandle.AddrOfPinnedObject() + sourceStart, sourceStride,
                    destinationHandle.AddrOfPinnedObject(), (uint)destinationStride, (uint)rowSize, height);
            }
            finally
            {
                destinationHandle.Free();
                sourceHandle.Free();
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < destinationStride; x++)
                    Assert.That(destination[y * destinationStride + x], Is.EqualTo(x < rowSize ? (byte)(y * rowSize + x) : (byte)0xee), $"Row {y}, byte {x}");
            }
        }

        [TestCase(4, 8u, 5u, 1)]
        [TestCase(-4, 8u, 5u, 1)]
        [TestCase(8, 4u, 5u, 1)]
        [TestCase(8, 8u, 5u, -1)]
        public void TestInvalidRowLayout(int sourceStride, uint destinationStride, uint rowSize, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BasicPipeline.CopyTextureRows(IntPtr.Zero, sourceStride, IntPtr.Zero, destinationStride, rowSize, height));
        }
    }
}
