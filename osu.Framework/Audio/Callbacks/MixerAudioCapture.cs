// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;

namespace osu.Framework.Audio.Callbacks
{
    /// <summary>
    /// Single-consumer, fixed-format recording of a live manager's output mixers.
    /// Device restarts replace producers without replacing the capture timeline.
    /// </summary>
    public sealed class MixerAudioCapture : IDisposable
    {
        public const int SAMPLE_RATE = 44100;
        public const int CHANNELS = 2;

        private readonly MixerAudioCaptureHub hub;
        private readonly NativeWasapiRecordingSource? nativeSource;
        private int disposed;
        public long StartTimestamp { get; }
        public long Position { get; private set; }

        internal MixerAudioCapture(MixerAudioCaptureHub hub, long startTimestamp)
        {
            this.hub = hub;
            StartTimestamp = startTimestamp;
            if (OperatingSystem.IsWindows())
                nativeSource = new NativeWasapiRecordingSource(startTimestamp);
        }

        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

        /// <summary>
        /// Mix the next interval, filling unavailable audio with silence. Call from one encoder
        /// worker, allowing 100 ms of capture latency before consuming live intervals.
        /// </summary>
        public void Read(float[] destination, int frames)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (frames <= 0 || frames > SAMPLE_RATE || destination.Length < frames * CHANNELS)
                throw new ArgumentOutOfRangeException(nameof(frames));

            Array.Clear(destination, 0, frames * CHANNELS);
            foreach (var source in hub.Sources)
                source.Mix(this, destination, Position, frames);
            nativeSource?.Mix(destination, Position, frames);
            hub.ReleaseDrainedSources();
            for (int i = 0; i < frames * CHANNELS; i++)
                destination[i] = float.IsFinite(destination[i]) ? Math.Clamp(destination[i], -1, 1) : 0;
            Position += frames;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                nativeSource?.Dispose();
                hub.End(this);
            }
        }
    }
}
