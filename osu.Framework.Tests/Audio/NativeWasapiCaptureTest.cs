// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using NUnit.Framework;
using osu.Framework.Audio.Callbacks;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    [NonParallelizable]
    public class NativeWasapiCaptureTest
    {
        [Test]
        [Explicit("Opens the default WASAPI device exclusively for one second, playing silence.")]
        public void TestExclusiveDeviceCapture()
        {
            Assert.That(OperatingSystem.IsWindows(), Is.True);
            Assert.That(Bass.Init(0), Is.True);
            int mixer = 0;
            IntPtr bassLibrary = IntPtr.Zero;
            bool sawBlock = false;
            try
            {
                bassLibrary = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "bass.dll"));
                Assert.That(NativeWasapiCapture.TryCreate(NativeLibrary.GetExport(bassLibrary, "BASS_ChannelGetData"), out nint callback, out nint context), Is.True);
                Assert.That(BassWasapi.InitEx(-1, 48000, 2, WasapiInitFlags.Exclusive | WasapiInitFlags.EventDriven | WasapiInitFlags.AutoFormat,
                    0.01f, 0.005f, callback, context), Is.True, $"WASAPI init: {Bass.LastError}");
                Assert.That(BassWasapi.GetInfo(out var info), Is.True);
                Assert.That((info.InitFlags & WasapiInitFlags.Exclusive) != 0, Is.True);
                mixer = BassMix.CreateMixerStream(info.Frequency, info.Channels, BassFlags.Decode | BassFlags.Float | BassFlags.MixerNonStop);
                Assert.That(mixer, Is.Not.Zero);
                NativeWasapiCapture.Configure(info.Frequency, info.Channels);
                NativeWasapiCapture.SetMixer(mixer);
                Assert.That(NativeWasapiCapture.SetEnabled(true), Is.True);
                Assert.That(BassWasapi.Start(), Is.True);
                byte[] buffer = new byte[65536];
                var timer = Stopwatch.StartNew();
                ulong lastPosition = 0;
                int count = 0;
                while (timer.ElapsedMilliseconds < 1000)
                {
                    if (NativeWasapiCapture.TryRead(buffer, out var block, out _))
                    {
                        Assert.That(block.Frequency, Is.EqualTo(info.Frequency));
                        Assert.That(block.Channels, Is.EqualTo(info.Channels));
                        Assert.That(block.Length, Is.GreaterThan(0));
                        if (sawBlock)
                            Assert.That(block.SamplePosition, Is.GreaterThan(lastPosition));
                        lastPosition = block.SamplePosition;
                        sawBlock = true;
                        count++;
                    }
                    else
                        Thread.Sleep(1);
                }

                TestContext.WriteLine($"Exclusive WASAPI: {info.Frequency} Hz, {info.Channels} channels, {count} captured blocks.");
                Assert.That(sawBlock, Is.True);
            }
            finally
            {
                BassWasapi.Stop();
                BassWasapi.Free();
                NativeWasapiCapture.Stopped();
                NativeWasapiCapture.SetMixer(0);
                if (mixer != 0)
                    Bass.StreamFree(mixer);
                NativeWasapiCapture.FreeAfterWasapiStopped();
                Bass.Free();
                if (bassLibrary != IntPtr.Zero)
                    NativeLibrary.Free(bassLibrary);
            }
        }

        [Test]
        public void TestBlockAbi() => Assert.That(Marshal.SizeOf<WasapiCaptureBlock>(), Is.EqualTo(32));

        [Test]
        [Explicit("Requires G0V0_WASAPI_CAPTURE_LIBRARY pointing to the built native DLL; no audio device is opened.")]
        public void TestNativeInteropAndDeviceReset()
        {
            MixCallback mixer = mix;
            Assert.That(NativeWasapiCapture.TryCreate(Marshal.GetFunctionPointerForDelegate(mixer), out nint callback, out nint context), Is.True);
            IntPtr pcm = Marshal.AllocHGlobal(32);
            try
            {
                NativeWasapiCapture.Configure(48000, 2);
                NativeWasapiCapture.SetMixer(7);
                Assert.That(NativeWasapiCapture.SetEnabled(true), Is.True);
                var produce = Marshal.GetDelegateForFunctionPointer<CaptureCallback>(callback);
                Assert.That(produce(pcm, 32, context), Is.EqualTo(32));
                Span<byte> destination = stackalloc byte[65536];
                Assert.That(NativeWasapiCapture.TryRead(destination, out var block, out long generation), Is.True);
                Assert.That(block.Length, Is.EqualTo(32));
                Assert.That(block.Frequency, Is.EqualTo(48000));
                Assert.That(block.Channels, Is.EqualTo(2));
                Assert.That(destination[31], Is.EqualTo(0x5a));
                NativeWasapiCapture.Stopped();
                Assert.That(NativeWasapiCapture.SetEnabled(true), Is.False);
                NativeWasapiCapture.Configure(44100, 2);
                Assert.That(NativeWasapiCapture.SetEnabled(true), Is.True);
                Assert.That(produce(pcm, 32, context), Is.EqualTo(32));
                Assert.That(NativeWasapiCapture.TryRead(destination, out block, out long nextGeneration), Is.True);
                Assert.That(nextGeneration, Is.GreaterThan(generation));
                Assert.That(block.SamplePosition, Is.Zero);
                Assert.That(block.Frequency, Is.EqualTo(44100));
            }
            finally
            {
                NativeWasapiCapture.Stopped();
                NativeWasapiCapture.FreeAfterWasapiStopped();
                Marshal.FreeHGlobal(pcm);
                GC.KeepAlive(mixer);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint MixCallback(uint handle, IntPtr buffer, uint length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint CaptureCallback(IntPtr buffer, uint length, IntPtr context);

        private static uint mix(uint handle, IntPtr buffer, uint length)
        {
            for (int i = 0; i < length; i++)
                Marshal.WriteByte(buffer, i, 0x5a);
            return length;
        }
    }
}
