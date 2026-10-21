// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Audio.Mixing.Bass;
using NUnit.Framework;
using osu.Framework.Audio.Mixing;
using osu.Framework.Development;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    [NonParallelizable]
    public class OfflineAudioMixerTest
    {
        [Test]
        public void MixesDecodeStreamWithoutRealtimePlayback()
        {
            bool wasAudioThread = ThreadSafety.IsAudioThread;
            int previousDevice = Bass.CurrentDevice;
            bool initialised = Bass.Init(0);
            Assert.That(initialised || Bass.LastError == Errors.Already, Is.True);
            Bass.CurrentDevice = 0;
            ThreadSafety.IsAudioThread = true;
            int stream = 0;
            long generatedSamples = 0;
            StreamProcedure source = (_, buffer, length, _) =>
            {
                var data = new float[length / sizeof(float)];
                for (int i = 0; i < data.Length; i++)
                    data[i] = 0.125f;
                Marshal.Copy(data, 0, buffer, data.Length);
                generatedSamples += data.Length / 2;
                return length;
            };
            try
            {
                using var mixer = new OfflineAudioMixer();
                stream = Bass.CreateStream(48000, 2, BassFlags.Decode | BassFlags.Float, source);
                Assert.That(stream, Is.Not.Zero);
                Assert.That(BassMix.MixerAddChannel(((BassAudioMixer)mixer.Mixer).Handle, stream, BassFlags.MixerChanNoRampin), Is.True);
                var output = new float[1600];
                for (int i = 0; i < 600; i++)
                {
                    mixer.Read(output, 800);
                    Assert.That(output, Is.All.EqualTo(0.125f).Within(0.0001));
                }
                Assert.That(generatedSamples, Is.EqualTo(480000));
                GC.KeepAlive(source);
            }
            finally
            {
                if (stream != 0)
                    Bass.StreamFree(stream);
                ThreadSafety.IsAudioThread = wasAudioThread;
                if (initialised)
                    Bass.Free();
                if (previousDevice >= 0)
                    Bass.CurrentDevice = previousDevice;
            }
        }

        [Test]
        public void PullsExactSilenceWithoutSoundDevice()
        {
            bool wasAudioThread = ThreadSafety.IsAudioThread;
            int previousDevice = Bass.CurrentDevice;
            bool initialised = Bass.Init(0);
            Assert.That(initialised || Bass.LastError == Errors.Already, Is.True);
            Bass.CurrentDevice = 0;
            ThreadSafety.IsAudioThread = true;
            try
            {
                using var mixer = new OfflineAudioMixer();
                var samples = new float[1600];
                for (int i = 0; i < 600; i++)
                {
                    System.Array.Fill(samples, 1f);
                    mixer.Read(samples, 800);
                    Assert.That(samples, Is.All.Zero);
                }
            }
            finally
            {
                ThreadSafety.IsAudioThread = wasAudioThread;
                if (initialised)
                    Bass.Free();
                if (previousDevice >= 0)
                    Bass.CurrentDevice = previousDevice;
            }
        }

        [Test]
        public void MixesSampleChannelWithoutRealtimePlayback()
        {
            bool wasAudioThread = ThreadSafety.IsAudioThread;
            int previousDevice = Bass.CurrentDevice;
            bool initialised = Bass.Init(0);
            Assert.That(initialised || Bass.LastError == Errors.Already, Is.True);
            Bass.CurrentDevice = 0;
            ThreadSafety.IsAudioThread = true;

            // Generate a simple 16-bit mono 48000 Hz WAV with 800 samples of 0.25 amplitude
            byte[] wavData;
            using (var ms = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(ms))
            {
                short[] pcmSamples = new short[800];
                for (int i = 0; i < pcmSamples.Length; i++)
                    pcmSamples[i] = 8192; // 0.25 of short.MaxValue

                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + pcmSamples.Length * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write((short)1); // Mono
                writer.Write(48000);   // SampleRate
                writer.Write(48000 * 2); // ByteRate
                writer.Write((short)2); // BlockAlign
                writer.Write((short)16); // BitsPerSample
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(pcmSamples.Length * 2);
                for (int i = 0; i < pcmSamples.Length; i++)
                    writer.Write(pcmSamples[i]);

                wavData = ms.ToArray();
            }

            int sampleId = 0;
            try
            {
                using var handle = new GCHandleWrapper(GCHandle.Alloc(wavData, GCHandleType.Pinned));
                sampleId = Bass.SampleLoad(handle.Handle.AddrOfPinnedObject(), 0, wavData.Length, 2, BassFlags.Default);
                Assert.That(sampleId, Is.Not.Zero, $"SampleLoad failed: {Bass.LastError}");

                using var mixer = new OfflineAudioMixer();
                Assert.That(mixer.PlaySample(sampleId), Is.True);

                var output = new float[1600];
                mixer.Read(output, 800);
                Assert.That(output, Has.Some.Not.Zero);

                // Subsequent frames should return silence since sample ended
                mixer.Read(output, 800);
                Assert.That(output, Is.All.Zero);

                // Test PlaySample(byte[]) overload
                Assert.That(mixer.PlaySample(wavData), Is.True);
                mixer.Read(output, 800);
                Assert.That(output, Has.Some.Not.Zero);

                mixer.Read(output, 800);
                Assert.That(output, Is.All.Zero);
            }
            finally
            {
                if (sampleId != 0)
                    Bass.SampleFree(sampleId);
                ThreadSafety.IsAudioThread = wasAudioThread;
                if (initialised)
                    Bass.Free();
                if (previousDevice >= 0)
                    Bass.CurrentDevice = previousDevice;
            }
        }

        [Test]
        public void MixesDirectDecodeChannel()
        {
            bool wasAudioThread = ThreadSafety.IsAudioThread;
            int previousDevice = Bass.CurrentDevice;
            bool initialised = Bass.Init(0);
            Assert.That(initialised || Bass.LastError == Errors.Already, Is.True);
            Bass.CurrentDevice = 0;
            ThreadSafety.IsAudioThread = true;
            int stream = 0;
            try
            {
                using var mixer = new OfflineAudioMixer();
                stream = Bass.CreateStream(48000, 2, BassFlags.Decode | BassFlags.Float, (_, buffer, length, _) =>
                {
                    var data = new float[length / sizeof(float)];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = 0.5f;
                    Marshal.Copy(data, 0, buffer, data.Length);
                    return length;
                });
                Assert.That(stream, Is.Not.Zero);
                Assert.That(mixer.AddChannel(stream), Is.True);

                var output = new float[1600];
                mixer.Read(output, 800);
                Assert.That(output, Is.All.EqualTo(0.5f).Within(0.0001));
            }
            finally
            {
                if (stream != 0)
                    Bass.StreamFree(stream);
                ThreadSafety.IsAudioThread = wasAudioThread;
                if (initialised)
                    Bass.Free();
                if (previousDevice >= 0)
                    Bass.CurrentDevice = previousDevice;
            }
        }

        private sealed class GCHandleWrapper : IDisposable
        {
            public GCHandle Handle { get; }
            public GCHandleWrapper(GCHandle handle) => Handle = handle;
            public void Dispose() { if (Handle.IsAllocated) Handle.Free(); }
        }
    }
}
