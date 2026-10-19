// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Veldrid;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class OfflineDeviceTest
    {
        [Test]
        [Explicit("Requires Windows D3D11 hardware.")]
        public void RendersWithoutWindowOrSwapchain()
        {
            bool previous = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            var renderer = new VeldridRenderer();
            try
            {
                renderer.InitialiseOffline(640, 360);
                Assert.That(renderer.IsInitialised, Is.True);
                Assert.That(((IVeldridRenderer)renderer).Device.MainSwapchain, Is.Null);
                renderer.BeginFrame(new Vector2(640, 360));
                renderer.Clear(new ClearInfo(Color4.Red));
                renderer.FinishFrame();
                renderer.WaitUntilIdle();
                renderer.DisposeOffline();
            }
            finally
            {
                ThreadSafety.IsDrawThread = previous;
            }
        }
    }
}
