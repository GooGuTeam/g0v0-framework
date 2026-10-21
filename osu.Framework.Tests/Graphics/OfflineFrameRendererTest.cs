// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class OfflineFrameRendererTest
    {
        [TestCase(0, 1080)]
        [TestCase(1920, 0)]
        [TestCase(1919, 1080)]
        [TestCase(1920, 1079)]
        public void RejectsInvalidEncoderDimensions(int width, int height)
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            try
            {
                var renderer = new DummyRenderer();
                Assert.Throws<ArgumentException>(() => new OfflineFrameRenderer(renderer, width, height));
            }
            finally
            {
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [Test]
        public void RejectsWrongThread()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = false;
            try
            {
                Assert.Throws<InvalidOperationException>(() => new OfflineFrameRenderer(null!, 1920, 1080));
            }
            finally
            {
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }
    }
}
