// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;

namespace osu.Framework.Audio.Callbacks
{
    internal sealed class MixerAudioCaptureHub
    {
        private readonly object sync = new object();
        private MixerAudioCapture? current;
        private MixerAudioCaptureSource[] sources = Array.Empty<MixerAudioCaptureSource>();

        public MixerAudioCapture? Current => Volatile.Read(ref current);
        public MixerAudioCaptureSource[] Sources => Volatile.Read(ref sources);

        public MixerAudioCapture Begin(long timestamp)
        {
            lock (sync)
            {
                if (current != null)
                    throw new InvalidOperationException(@"An audio capture is already active.");
                var capture = new MixerAudioCapture(this, timestamp);
                Volatile.Write(ref current, capture);
                return capture;
            }
        }

        public void End(MixerAudioCapture capture)
        {
            lock (sync)
            {
                if (ReferenceEquals(current, capture))
                {
                    Volatile.Write(ref current, null);
                    Volatile.Write(ref sources, sources.Where(s => !s.IsRetired).ToArray());
                }
            }
        }

        public MixerAudioCaptureSource AddSource()
        {
            lock (sync)
            {
                // Bound the memory footprint even with custom rulesets creating many mixers.
                if (sources.Length >= 64)
                    throw new InvalidOperationException(@"The audio capture mixer limit was reached.");
                var source = new MixerAudioCaptureSource(this);
                var updated = new MixerAudioCaptureSource[sources.Length + 1];
                sources.CopyTo(updated, 0);
                updated[^1] = source;
                Volatile.Write(ref sources, updated);
                return source;
            }
        }

        public void RemoveSource(MixerAudioCaptureSource source)
        {
            source.Retire();
            lock (sync)
            {
                // Keep queued audio across device restart until the encoder has consumed it.
                if (current == null)
                    Volatile.Write(ref sources, sources.Where(s => !ReferenceEquals(s, source)).ToArray());
            }
        }

        public void ReleaseDrainedSources()
        {
            lock (sync)
            {
                if (sources.Any(s => s.CanRelease))
                    Volatile.Write(ref sources, sources.Where(s => !s.CanRelease).ToArray());
            }
        }
    }
}
