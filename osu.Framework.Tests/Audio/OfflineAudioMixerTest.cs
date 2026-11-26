// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Mix;
using NUnit.Framework;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Audio.Sample;
using osu.Framework.Development;
using osu.Framework.IO.Stores;

namespace osu.Framework.Tests.Audio
{
    [TestFixture]
    [NonParallelizable]
    public class OfflineAudioMixerTest
    {
        [Test]
        public void SeeksSampleChannelsBeforePlaybackAndAfterCompletion()
        {
            // Three distinct PCM sections make a wrong seek position audible in the mixed output.
            byte[] wav;

            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + 144000 * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(48000);
                writer.Write(96000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(144000 * 2);
                for (int i = 0; i < 144000; i++)
                    writer.Write((short)((i / 48000 + 1) * 4096));
                wav = stream.ToArray();
            }

            using var mixer = new OfflineAudioMixer();
            var factory = Task.Run(() => new SampleBassFactory(wav, "timeline", (BassAudioMixer)mixer.Mixer)).GetAwaiter().GetResult();
            var sample = factory.CreateSample();
            var channel = Task.Run(sample.GetChannel).GetAwaiter().GetResult();

            try
            {
                float[] output = new float[1600];

                Assert.That(sample.Length, Is.Zero);
                Assert.That(channel.Seek(3100), Is.False);
                factory.Update();

                Assert.That(Task.Run(() => channel.Seek(1500)).GetAwaiter().GetResult(), Is.True);
                factory.Update();
                factory.Update();
                mixer.Read(output, 800);
                Assert.That(channel.CurrentTime, Is.EqualTo(1500));
                Assert.That(channel.Playing, Is.False);
                Assert.That(output, Is.All.Zero);

                channel.ManualFree = true;
                channel.Play();
                factory.Update();
                mixer.Read(output, 800);
                Assert.That(output, Is.All.EqualTo(0.25f).Within(0.001));

                channel.Stop();
                factory.Update();
                mixer.Read(output, 800);
                Assert.That(output, Is.All.Zero);

                Assert.That(channel.Seek(500), Is.True);
                channel.Play();
                factory.Update();
                mixer.Read(output, 800);
                Assert.That(output, Is.All.EqualTo(0.125f).Within(0.001));

                Assert.That(channel.Seek(2990), Is.True);
                factory.Update();
                mixer.Read(output, 800);
                factory.Update();
                Assert.That(channel.Playing, Is.False);
                Assert.That(channel.Seek(2500), Is.True);
                channel.Play();
                factory.Update();
                mixer.Read(output, 800);
                Assert.That(output, Is.All.EqualTo(0.375f).Within(0.001));
                Assert.That(channel.Seek(double.NaN), Is.False);
                Assert.That(channel.Seek(-1), Is.False);
                Assert.That(channel.Seek(3000), Is.False);
            }
            finally
            {
                channel.Dispose();
                channel.Update();
                factory.Dispose();
                factory.Update();
            }
        }

        [Test]
        public void RestoresNoSoundDeviceStateAfterLastOfflineMixer()
        {
            Assert.That(Bass.GetDeviceInfo(Bass.NoSoundDevice, out var before), Is.True);

            using (var mixer = new OfflineAudioMixer())
            {
                AudioTestHelper.RunOnAudioThread(() =>
                {
                    using var second = new OfflineAudioMixer();
                    second.Read(new float[1600], 800);
                });

                Assert.That(Bass.GetDeviceInfo(Bass.NoSoundDevice, out var during), Is.True);
                Assert.That(during.IsInitialized, Is.True);
                mixer.Read(new float[1600], 800);

                using var sameThreadMixer = new OfflineAudioMixer();
                mixer.Dispose();
                sameThreadMixer.Read(new float[1600], 800);
            }

            Assert.That(Bass.GetDeviceInfo(Bass.NoSoundDevice, out var after), Is.True);
            Assert.That(after.IsInitialized, Is.EqualTo(before.IsInitialized));
        }

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
                float[] data = new float[length / sizeof(float)];
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
                float[] output = new float[1600];

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
                float[] samples = new float[1600];

                for (int i = 0; i < 600; i++)
                {
                    Array.Fill(samples, 1f);
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
                writer.Write(48000); // SampleRate
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

                float[] output = new float[1600];
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
                    float[] data = new float[length / sizeof(float)];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = 0.5f;
                    Marshal.Copy(data, 0, buffer, data.Length);
                    return length;
                });
                Assert.That(stream, Is.Not.Zero);
                Assert.That(mixer.AddChannel(stream), Is.True);

                float[] output = new float[1600];
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

        [Test]
        public void TrackCompletionInOfflineMixer()
        {
            int previousDevice = Bass.CurrentDevice;
            bool initialised = Bass.Init(Bass.NoSoundDevice);
            Assert.That(initialised || Bass.LastError == Errors.Already, Is.True);
            Bass.CurrentDevice = Bass.NoSoundDevice;

            try
            {
                using var mixer = new OfflineAudioMixer();
                using var resources = new ResourceStore<byte[]>(new DllResourceStore(typeof(OfflineAudioMixerTest).Assembly));
                var audio = mixer.CreateAudioManager(resources);

                try
                {
                    var track = audio.Tracks.Get(@"Resources.Tracks.sample-track.mp3");
                    audio.Update();
                    Assert.That(track.IsLoaded, Is.True);
                    track.Seek(Math.Max(0, track.Length - 100));
                    track.Start();
                    float[] output = new float[400];

                    for (int i = 0; i < 240; i++)
                    {
                        audio.Update();
                        mixer.Read(output, 200);
                    }

                    audio.Update();
                    Assert.That(track.HasCompleted, Is.True);
                }
                finally
                {
                    audio.Dispose();
                    audio.Update();
                }
            }
            finally
            {
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

            public void Dispose()
            {
                if (Handle.IsAllocated) Handle.Free();
            }
        }
    }
}
