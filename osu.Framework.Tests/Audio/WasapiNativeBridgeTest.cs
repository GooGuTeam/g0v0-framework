// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Audio.Callbacks;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    public class WasapiNativeBridgeTest
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint WasapiProc(IntPtr buffer, uint length, IntPtr user);

        private const string capture_library_variable = @"G0V0_WASAPI_CAPTURE_LIBRARY";

        private string? captureLibrary;

        /// <summary>
        /// Skips a test that needs <c>g0v0.Audio.dll</c>, which only exists where the native recording libraries were built or downloaded.
        /// </summary>
        private void requireNativeLibrary()
        {
            string path = string.IsNullOrEmpty(captureLibrary) ? Path.Combine(AppContext.BaseDirectory, "g0v0.Audio.dll") : captureLibrary;

            if (!File.Exists(path))
                Assert.Ignore($"The native capture library was not found at {path}.");
        }

        [SetUp]
        public void SetUp()
        {
            captureLibrary = Environment.GetEnvironmentVariable(capture_library_variable);
        }

        [TearDown]
        public void TearDown()
        {
            WasapiNativeBridge.Free();
            Environment.SetEnvironmentVariable(capture_library_variable, captureLibrary);
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

            requireNativeLibrary();

            bool success = WasapiNativeBridge.TryGetNativeProcedure(out IntPtr proc, out IntPtr context);

            Assert.IsTrue(success);
            Assert.AreNotEqual(IntPtr.Zero, proc);
            Assert.AreNotEqual(IntPtr.Zero, context);
            Assert.IsTrue(WasapiNativeBridge.IsNativeActive);
            Assert.IsNotEmpty(WasapiNativeBridge.ProviderName);
            TestContext.Out.WriteLine($"Active WASAPI Provider: {WasapiNativeBridge.ProviderName}");
        }

        [Test]
        public void TestManagedCallbackWithoutNativeLibrary()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            Environment.SetEnvironmentVariable(capture_library_variable, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll"));

            Assert.IsFalse(WasapiNativeBridge.TryGetNativeProcedure(out IntPtr proc, out IntPtr context));
            Assert.AreEqual(IntPtr.Zero, proc);
            Assert.AreEqual(IntPtr.Zero, context);
            Assert.IsFalse(WasapiNativeBridge.IsNativeActive);
        }

        [Test]
        public void TestDirectNativeInvocationReturnsZeroWhenInactive()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                return;

            requireNativeLibrary();

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

            requireNativeLibrary();

            Assert.IsTrue(WasapiNativeBridge.TryGetNativeProcedure(out _, out _));
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
