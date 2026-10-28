// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace osu.Framework.Audio.Callbacks
{
    /// <summary>
    /// Drains the native output tap independently of encoding. Conversion and buffering
    /// never run on the playback callback or introduce another playback mixer.
    /// </summary>
    internal sealed class NativeWasapiRecordingSource : IDisposable
    {
        private const int capacity = MixerAudioCapture.SAMPLE_RATE * 2;
        private static int owned;
        private readonly object sync = new object();
        private readonly byte[] buffer = new byte[65536];
        private readonly float[] samples = new float[capacity * MixerAudioCapture.CHANNELS];
        private readonly long[] positions = new long[capacity];
        private readonly ManualResetEventSlim stop = new ManualResetEventSlim();
        private readonly Thread reader;
        private readonly long startTimestamp;
        private Exception? failure;
        private long generation = -1;
        private double origin;
        private double nextPosition;
        private int disposed;

        public NativeWasapiRecordingSource(long startTimestamp)
        {
            this.startTimestamp = startTimestamp;
            if (Interlocked.CompareExchange(ref owned, 1, 0) != 0)
                throw new InvalidOperationException(@"The native audio capture already has a recording consumer.");

            try
            {
                Array.Fill(positions, -1);
                NativeWasapiCapture.SetEnabled(false);
                for (int i = 0; i < 32 && NativeWasapiCapture.TryRead(buffer, out _, out _); i++)
                {
                }
                NativeWasapiCapture.SetEnabled(true);
                reader = new Thread(run) { IsBackground = true, Name = @"WASAPI recording capture" };
                reader.Start();
            }
            catch
            {
                NativeWasapiCapture.SetEnabled(false);
                stop.Dispose();
                Volatile.Write(ref owned, 0);
                throw;
            }
        }

        private void run()
        {
            try
            {
                while (!stop.IsSet)
                {
                    // Bound each drain so cancellation remains responsive under continuous input.
                    for (int i = 0; i < 64 && !stop.IsSet; i++)
                    {
                        if (!NativeWasapiCapture.TryRead(buffer, out var block, out long currentGeneration))
                            break;
                        store(block, currentGeneration);
                    }
                    stop.Wait(2);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure, ex);
            }
        }

        private void store(WasapiCaptureBlock block, long currentGeneration)
        {
            if (block.Qpc <= startTimestamp || block.Frequency == 0 || block.Channels == 0 || block.Channels > 32
                || block.Length == 0 || block.Length > buffer.Length || block.Length % (block.Channels * sizeof(float)) != 0)
                return;

            double ratio = (double)MixerAudioCapture.SAMPLE_RATE / block.Frequency;
            int channels = (int)block.Channels;
            int count = (int)(block.Length / (block.Channels * sizeof(float)));
            double measuredStart = (block.Qpc - startTimestamp) * (double)MixerAudioCapture.SAMPLE_RATE / Stopwatch.Frequency - count * ratio;
            double mappedStart = origin + block.SamplePosition * ratio;
            if (generation != currentGeneration || measuredStart - mappedStart > MixerAudioCapture.SAMPLE_RATE / 50)
            {
                origin = Math.Max(nextPosition, measuredStart) - block.SamplePosition * ratio;
                generation = currentGeneration;
            }
            long firstOutput = (long)Math.Round(origin + block.SamplePosition * ratio);
            long endOutput = (long)Math.Round(origin + (block.SamplePosition + (ulong)count) * ratio);
            nextPosition = origin + (block.SamplePosition + (ulong)count) * ratio;
            ReadOnlySpan<float> input = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, (int)block.Length));

            lock (sync)
            {
                // Absolute position tags prevent wrapped, stale audio from being replayed after a stall.
                for (long sample = Math.Max(0, Math.Max(firstOutput, endOutput - capacity)); sample < endOutput; sample++)
                {
                    double sourcePosition = (sample - origin) * block.Frequency / MixerAudioCapture.SAMPLE_RATE - block.SamplePosition;
                    sourcePosition = Math.Clamp(sourcePosition, 0, count - 1);
                    int first = (int)sourcePosition;
                    int second = Math.Min(first + 1, count - 1);
                    float fraction = (float)(sourcePosition - first);
                    int slot = (int)(sample % capacity);
                    for (int channel = 0; channel < MixerAudioCapture.CHANNELS; channel++)
                    {
                        // Mono is duplicated; multichannel devices contribute their front stereo pair.
                        int inputChannel = Math.Min(channel, channels - 1);
                        float a = input[first * channels + inputChannel];
                        float b = input[second * channels + inputChannel];
                        samples[slot * MixerAudioCapture.CHANNELS + channel] = a + (b - a) * fraction;
                    }
                    positions[slot] = sample;
                }
            }
        }

        public void Mix(float[] destination, long position, int frames)
        {
            if (Volatile.Read(ref failure) is Exception error)
                throw new InvalidOperationException(@"Native recording audio capture failed.", error);

            lock (sync)
            {
                for (int i = 0; i < frames; i++)
                {
                    long sample = position + i;
                    int slot = (int)(sample % capacity);
                    if (positions[slot] != sample)
                        continue;
                    // Native final output replaces queued pre-switch mixer data, avoiding double mixing.
                    destination[i * 2] = samples[slot * 2];
                    destination[i * 2 + 1] = samples[slot * 2 + 1];
                    positions[slot] = -1;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            stop.Set();
            reader.Join();
            NativeWasapiCapture.SetEnabled(false);
            stop.Dispose();
            Volatile.Write(ref owned, 0);
        }
    }
}
