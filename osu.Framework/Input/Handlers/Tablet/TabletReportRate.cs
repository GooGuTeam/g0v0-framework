// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;

namespace osu.Framework.Input.Handlers.Tablet
{
    /// <summary>
    /// Bounded rolling one-second report count in 10 ms buckets, independent of rendering.
    /// </summary>
    internal sealed class TabletReportRate
    {
        private readonly object sync = new object();
        private readonly long[] epochs = new long[100];
        private readonly int[] counts = new int[100];
        private static long currentBucket => (long)(Stopwatch.GetTimestamp() * 100.0 / Stopwatch.Frequency);

        public void Record()
        {
            lock (sync)
            {
                long now = currentBucket;
                int index = (int)(now % counts.Length);
                if (epochs[index] != now)
                {
                    epochs[index] = now;
                    counts[index] = 0;
                }
                counts[index]++;
            }
        }

        public int Read()
        {
            lock (sync)
            {
                long now = currentBucket;
                int count = 0;
                for (int i = 0; i < counts.Length; i++)
                {
                    if (now - epochs[i] >= 0 && now - epochs[i] < counts.Length)
                        count += counts[i];
                }
                return count;
            }
        }

        public void Reset()
        {
            lock (sync)
                Array.Clear(counts);
        }
    }
}
