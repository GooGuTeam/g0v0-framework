// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using ManagedBass;
using osu.Framework.Audio.Mixing.Bass;
using osu.Framework.Development;

namespace osu.Framework.Audio.Mixing
{
    /// <summary>
    /// Private pull-driven 48 kHz stereo mixer. It is never connected to a speaker or
    /// the global WASAPI mixer. Use private track/sample stores; never move live channels here.
    /// All operations (including disposal) must be executed on the audio thread.
    /// </summary>
    public sealed class OfflineAudioMixer : IDisposable
    {
        private readonly BassAudioMixer mixer;
        private bool disposed;
        private readonly int ownerThread;
        private readonly bool previousAudioThread;
        private readonly int previousDevice;

        public AudioMixer Mixer => mixer;
        public int Handle => mixer.Handle;

        public OfflineAudioMixer()
        {
            ownerThread = Environment.CurrentManagedThreadId;
            previousAudioThread = ThreadSafety.IsAudioThread;
            ThreadSafety.IsAudioThread = true;
            previousDevice = ManagedBass.Bass.CurrentDevice;
            try
            {
                ensureBassDevice();
                mixer = new BassAudioMixer(null, null, @"Offline export", offline: true);
                mixer.Update();
                if (mixer.Handle == 0)
                {
                    mixer.Dispose();
                    mixer.Update();
                    throw new InvalidOperationException($@"Unable to create offline mixer: {ManagedBass.Bass.LastError}");
                }
            }
            catch
            {
                if (previousDevice >= 0)
                    ManagedBass.Bass.CurrentDevice = previousDevice;
                ThreadSafety.IsAudioThread = previousAudioThread;
                throw;
            }
        }

        private static void ensureBassDevice()
        {
            if (!ManagedBass.Bass.Init(ManagedBass.Bass.NoSoundDevice) && ManagedBass.Bass.LastError != Errors.Already)
                throw new InvalidOperationException($@"Unable to initialise the private audio device: {ManagedBass.Bass.LastError}");
            ManagedBass.Bass.CurrentDevice = ManagedBass.Bass.NoSoundDevice;
        }

        /// <summary>
        /// Creates private track and sample stores preserving normal channel adjustments,
        /// looping and stop behaviour. Dispose the manager before disposing this mixer.
        /// </summary>
        public AudioManager CreateAudioManager(osu.Framework.IO.Stores.ResourceStore<byte[]> resources)
        {
            requireAudioThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            return new AudioManager(resources, mixer);
        }

        /// <summary>
        /// Pull exactly <paramref name="frames"/> sample frames into interleaved float PCM.
        /// Caller must first process all private stores/channels' queued events for this interval.
        /// </summary>
        public void Read(float[] output, int frames)
        {
            requireAudioThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(frames);
            if (output.Length < checked(frames * 2))
                throw new ArgumentException("PCM buffer is too small.", nameof(output));
            if (frames == 0)
                return;
            mixer.Update();
            int bytes = checked(frames * 2 * sizeof(float));
            int read = ManagedBass.Bass.ChannelGetData(mixer.Handle, output, bytes | (int)DataFlags.Float);
            if (read != bytes)
                throw new InvalidOperationException($"Offline mixing returned {read}/{bytes} bytes: {ManagedBass.Bass.LastError}");
        }

        private readonly System.Collections.Generic.Dictionary<byte[], int> loadedSamples = new System.Collections.Generic.Dictionary<byte[], int>();

        /// <summary>
        /// Adds a decode-only channel (such as an offline track stream) directly to this offline mixer.
        /// </summary>
        public bool AddChannel(int channelHandle)
        {
            requireAudioThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (channelHandle == 0) return false;

            return ManagedBass.Mix.BassMix.MixerAddChannel(mixer.Handle, channelHandle, ManagedBass.BassFlags.MixerChanNoRampin);
        }

        /// <summary>
        /// Mixes an offline sample channel directly into this offline mixer using a loaded sample ID.
        /// </summary>
        public bool PlaySample(int sampleId)
        {
            requireAudioThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sampleId == 0) return false;

            int channel = ManagedBass.Bass.SampleGetChannel(sampleId, ManagedBass.BassFlags.SampleChannelStream | ManagedBass.BassFlags.Decode);
            if (channel == 0) return false;

            return ManagedBass.Mix.BassMix.MixerAddChannel(mixer.Handle, channel, ManagedBass.BassFlags.MixerChanNoRampin);
        }

        /// <summary>
        /// Mixes an offline sample channel directly into this offline mixer using raw encoded sample data.
        /// </summary>
        public bool PlaySample(byte[] data)
        {
            requireAudioThread();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (data == null || data.Length == 0) return false;

            if (!loadedSamples.TryGetValue(data, out int sampleId))
            {
                using var handle = new osu.Framework.Allocation.ObjectHandle<byte[]>(data, System.Runtime.InteropServices.GCHandleType.Pinned);
                sampleId = ManagedBass.Bass.SampleLoad(handle.Address, 0, data.Length, 8, ManagedBass.BassFlags.Default);
                if (sampleId == 0) return false;
                loadedSamples[data] = sampleId;
            }

            return PlaySample(sampleId);
        }

        public void Dispose()
        {
            requireAudioThread();
            if (disposed)
                return;
            disposed = true;
            foreach (int sampleId in loadedSamples.Values)
                ManagedBass.Bass.SampleFree(sampleId);
            loadedSamples.Clear();
            try
            {
                mixer.Dispose();
                mixer.Update();
            }
            finally
            {
                if (previousDevice >= 0)
                    ManagedBass.Bass.CurrentDevice = previousDevice;
                ThreadSafety.IsAudioThread = previousAudioThread;
            }
        }

        private void requireAudioThread()
        {
            if (!ThreadSafety.IsAudioThread || Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("Offline mixing must run on the audio thread.");
        }
    }
}
