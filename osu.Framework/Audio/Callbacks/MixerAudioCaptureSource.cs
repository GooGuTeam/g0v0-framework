// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;

namespace osu.Framework.Audio.Callbacks
{
    internal sealed class MixerAudioCaptureSource
    {
        private const int capacity = 32;
        private const int block_frames = 2048;
        private readonly MixerAudioCaptureHub hub;
        private readonly Block[] blocks = new Block[capacity];
        private long written;
        private long read;
        private MixerAudioCapture? epoch;
        private long nextPosition;
        private int retired;
        private int callbacks;

        public bool IsRetired => Volatile.Read(ref retired) != 0;

        public bool CanRelease => IsRetired && Volatile.Read(ref callbacks) == 0
                                  && Volatile.Read(ref read) == Volatile.Read(ref written);

        public void Retire() => Volatile.Write(ref retired, 1);

        public MixerAudioCaptureSource(MixerAudioCaptureHub hub)
        {
            this.hub = hub;
            for (int i = 0; i < capacity; i++)
                blocks[i] = new Block();
        }

        // BASS serialises DSP calls for each mixer. This callback allocates nothing, takes
        // no locks, never waits for the encoder and never changes the playback buffer.
        public unsafe void Capture(int handle, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (Volatile.Read(ref retired) != 0)
                return;

            Interlocked.Increment(ref callbacks);
            try
            {
                if (Volatile.Read(ref retired) == 0)
                    capture(buffer, length);
            }
            finally
            {
                Interlocked.Decrement(ref callbacks);
            }
        }

        private unsafe void capture(IntPtr buffer, int length)
        {
            var capture = hub.Current;
            if (capture == null || capture.IsDisposed || length <= 0 || length % (sizeof(float) * MixerAudioCapture.CHANNELS) != 0)
                return;

            int frames = length / (sizeof(float) * MixerAudioCapture.CHANNELS);
            long end = (long)((Stopwatch.GetTimestamp() - capture.StartTimestamp) * (double)MixerAudioCapture.SAMPLE_RATE / Stopwatch.Frequency);
            long measuredStart = end - frames;
            // Re-anchor after a new recording or stalled device. Small scheduling jitter
            // must not create gaps between otherwise contiguous DSP blocks.
            if (!ReferenceEquals(epoch, capture))
            {
                epoch = capture;
                nextPosition = measuredStart;
            }
            else if (measuredStart - nextPosition > MixerAudioCapture.SAMPLE_RATE / 50)
            {
                nextPosition = measuredStart;
            }
            // Do not re-anchor backwards: overlapping blocks would mix the same source twice.

            float* samples = (float*)buffer;
            for (int offset = 0; offset < frames;)
            {
                int count = Math.Min(block_frames, frames - offset);
                long index = written;
                if (index - Volatile.Read(ref read) < capacity)
                {
                    Block block = blocks[index % capacity];
                    new ReadOnlySpan<float>(samples + offset * MixerAudioCapture.CHANNELS, count * MixerAudioCapture.CHANNELS).CopyTo(block.Samples);
                    block.Capture = capture;
                    block.Position = nextPosition;
                    block.Frames = count;
                    Volatile.Write(ref written, index + 1);
                }
                // Overflow drops data, never time. The consumer fills the gap with silence.
                nextPosition += count;
                offset += count;
            }
        }

        public void Mix(MixerAudioCapture capture, float[] destination, long position, int frames)
        {
            long end = position + frames;
            while (read < Volatile.Read(ref written))
            {
                Block block = blocks[read % capacity];
                if (!ReferenceEquals(block.Capture, capture) || block.Position + block.Frames <= position)
                {
                    Volatile.Write(ref read, read + 1);
                    continue;
                }
                if (block.Position >= end) break;
                long first = Math.Max(position, block.Position);
                long last = Math.Min(end, block.Position + block.Frames);
                for (long sample = first; sample < last; sample++)
                {
                    int input = (int)(sample - block.Position) * MixerAudioCapture.CHANNELS;
                    int output = (int)(sample - position) * MixerAudioCapture.CHANNELS;
                    destination[output] += block.Samples[input];
                    destination[output + 1] += block.Samples[input + 1];
                }
                if (block.Position + block.Frames > end) break;
                Volatile.Write(ref read, read + 1);
            }
        }

        private sealed class Block
        {
            public readonly float[] Samples = new float[block_frames * MixerAudioCapture.CHANNELS];
            public MixerAudioCapture? Capture;
            public long Position;
            public int Frames;
        }
    }
}
