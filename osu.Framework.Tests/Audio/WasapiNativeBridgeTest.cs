// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Audio.Callbacks;
using osu.Framework.Platform;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    public class WasapiNativeBridgeTest
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint WasapiProc(IntPtr buffer, uint length, IntPtr user);

        [TearDown]
        public void TearDown()
        {
            WasapiNativeBridge.Free();
        }

        [Test]
        public void TestTryGetNativeProcedureOnWindows()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
            {
                Assert.IsFalse(WasapiNativeBridge.TryGetNativeProcedure(out _, out _));
                Assert.IsFalse(WasapiNativeBridge.IsNativeActive);
                return;
            }

            bool success = WasapiNativeBridge.TryGetNativeProcedure(out IntPtr proc, out IntPtr context);

            Assert.IsTrue(success);
            Assert.AreNotEqual(IntPtr.Zero, proc);
            Assert.AreNotEqual(IntPtr.Zero, context);
            Assert.IsTrue(WasapiNativeBridge.IsNativeActive);
            Assert.IsNotEmpty(WasapiNativeBridge.ProviderName);
            TestContext.Out.WriteLine($"Active WASAPI Provider: {WasapiNativeBridge.ProviderName}");
        }

        [Test]
        public void TestMixerHandlePublishAndReset()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            Assert.IsTrue(WasapiNativeBridge.TryGetNativeProcedure(out _, out IntPtr context));
            Assert.AreEqual(0, Marshal.ReadInt32(context));

            const int test_handle = 0x12345678;
            WasapiNativeBridge.SetMixerHandle(test_handle);
            Assert.AreEqual(test_handle, Marshal.ReadInt32(context));

            WasapiNativeBridge.Reset();
            Assert.AreEqual(0, Marshal.ReadInt32(context));
        }

        [Test]
        public void TestDirectNativeInvocationReturnsZeroWhenInactive()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            Assert.IsTrue(WasapiNativeBridge.TryGetNativeProcedure(out IntPtr proc, out IntPtr context));

            var wasapiProc = Marshal.GetDelegateForFunctionPointer<WasapiProc>(proc);

            // 1. With null user pointer
            uint bytesReadNull = wasapiProc(IntPtr.Zero, 512, IntPtr.Zero);
            Assert.AreEqual(0u, bytesReadNull);

            // 2. With inactive handle (0)
            WasapiNativeBridge.Reset();
            uint bytesReadInactive = wasapiProc(IntPtr.Zero, 512, context);
            Assert.AreEqual(0u, bytesReadInactive);
        }

        [Test]
        public void TestFreeAndReinitialisation()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            Assert.IsTrue(WasapiNativeBridge.TryGetNativeProcedure(out IntPtr proc1, out IntPtr ctx1));
            Assert.IsTrue(WasapiNativeBridge.IsNativeActive);

            WasapiNativeBridge.Free();
            Assert.IsFalse(WasapiNativeBridge.IsNativeActive);

            Assert.IsTrue(WasapiNativeBridge.TryGetNativeProcedure(out IntPtr proc2, out IntPtr ctx2));
            Assert.IsTrue(WasapiNativeBridge.IsNativeActive);
            Assert.AreNotEqual(IntPtr.Zero, proc2);
            Assert.AreNotEqual(IntPtr.Zero, ctx2);
        }
    }
}
