// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Development;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class GpuFrameCaptureTest
    {
        [Test]
        [Explicit("Requires Windows Vulkan hardware and the local Veldrid interop patches.")]
        public void TestVulkanInteropNegotiation()
        {
            // Reflection keeps tests buildable against the published Veldrid package.
            var optionField = typeof(VulkanDeviceOptions).GetField("EnableWin32CaptureInterop");
            Assert.That(optionField, Is.Not.Null);
            object boxedOptions = new VulkanDeviceOptions();
            optionField!.SetValue(boxedOptions, true);
            using var enabled = GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions(), (VulkanDeviceOptions)boxedOptions);
            var info = enabled.GetVulkanInfo();
            var capability = info.GetType().GetProperty("Win32CaptureInteropEnabled");
            Assert.That(capability, Is.Not.Null);
            TestContext.WriteLine($"Win32 capture interop negotiated: {capability!.GetValue(info)}");
            // A missing optional bundle must still permit creating a usable device.
            using var disabled = GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions());
            Assert.That(capability.GetValue(disabled.GetVulkanInfo()), Is.False);
            var submit = info.GetType().GetMethod("SubmitExternalCapture");
            Assert.That(submit, Is.Not.Null);
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                submit!.Invoke(disabled.GetVulkanInfo(), new object[] { IntPtr.Zero, IntPtr.Zero, 0UL }));
            Assert.That(error!.InnerException, Is.TypeOf<VeldridException>());
        }

        [TestCase(GraphicsBackend.Direct3D11)]
        [TestCase(GraphicsBackend.Vulkan)]
        [Explicit("Requires a local GPU and graphics drivers.")]
        public void TestGpuCopyAndBackpressure(GraphicsBackend backend)
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            try
            {
                using var device = backend == GraphicsBackend.Vulkan
                    ? GraphicsDevice.CreateVulkan(new GraphicsDeviceOptions())
                    : GraphicsDevice.CreateD3D11(new GraphicsDeviceOptions());
                using var source = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1,
                    PixelFormat.R8G8B8A8UNorm, TextureUsage.RenderTarget));
                using var framebuffer = device.ResourceFactory.CreateFramebuffer(new FramebufferDescription(null, source));
                using var commands = device.ResourceFactory.CreateCommandList();
                commands.Begin();
                commands.SetFramebuffer(framebuffer);
                commands.ClearColorTarget(0, new RgbaFloat(1, 0, 0, 1));
                commands.End();
                device.SubmitCommands(commands);

                using var session = new GpuFrameCaptureSession(device, 2);
                session.Capture(source);
                session.Capture(source);
                session.Capture(source);
                Assert.That(session.DroppedFrames, Is.EqualTo(1));
                device.WaitForIdle();
                Assert.That(session.TryAcquireFrame(out var frame), Is.True);
                using (frame!)
                {
                    using var readback = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D(16, 16, 1, 1,
                        source.Format, TextureUsage.Staging));
                    commands.Begin();
                    commands.CopyTexture(frame!.Texture, readback);
                    commands.End();
                    device.SubmitCommands(commands);
                    device.WaitForIdle();
                    var pixels = device.Map<uint>(readback, MapMode.Read);
                    try
                    {
                        Assert.That(pixels[0, 0], Is.EqualTo(0xff0000ffu));
                    }
                    finally
                    {
                        device.Unmap(readback);
                    }
                }

                session.Capture(source);
                Assert.That(session.DroppedFrames, Is.EqualTo(1));
            }
            finally
            {
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(17)]
        public void TestRejectInvalidCapacity(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GpuFrameCaptureSession(null!, capacity));
        }

        [Test]
        public void TestLeaseReleasedOnce()
        {
            int releases = 0;
            var frame = new GpuCapturedFrame(null!, 42, 1234, true, () => releases++);
            Assert.That(frame.FrameIndex, Is.EqualTo(42));
            Assert.That(frame.Timestamp, Is.EqualTo(1234));
            Assert.That(frame.IsUvOriginTopLeft, Is.True);
            frame.Dispose();
            frame.Dispose();
            Assert.That(releases, Is.EqualTo(1));
        }

        [Test]
        public void TestFailedReleaseCanBeRetried()
        {
            bool fail = true;
            int releases = 0;
            var frame = new GpuCapturedFrame(null!, 0, 0, true, () =>
            {
                if (fail)
                    throw new InvalidOperationException();
                releases++;
            });
            Assert.Throws<InvalidOperationException>(() => frame.Dispose());
            fail = false;
            frame.Dispose();
            Assert.That(releases, Is.EqualTo(1));
        }
    }
}
