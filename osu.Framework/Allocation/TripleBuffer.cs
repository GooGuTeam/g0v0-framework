// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;

namespace osu.Framework.Allocation
{
    /// <summary>
    /// Handles triple-buffering of any object type.
    /// Thread safety assumes at most one writer and one reader.
    /// Comes with the added assurance that the most recent <see cref="GetForRead"/> object is not written to.
    /// </summary>
    internal class TripleBuffer<T>
        where T : class
    {
        /// <summary>
        /// The default amount of time (in milliseconds) to wait for a write to occur during <see cref="GetForRead"/>.
        /// </summary>
        public const int DEFAULT_READ_TIMEOUT = 100;

        private const int buffer_count = 3;

        private readonly Buffer[] buffers = new Buffer[buffer_count];
        private readonly Stopwatch stopwatch = new Stopwatch();

        /// <summary>
        /// Set whenever a write finishes, so a reader waiting for one can block instead of spinning.
        /// </summary>
        private readonly ManualResetEventSlim writeFinished = new ManualResetEventSlim();

        private int writeIndex;
        private int flipIndex = 1;
        private int readIndex = 2;

        public TripleBuffer()
        {
            for (int i = 0; i < buffer_count; i++)
                buffers[i] = new Buffer(i, finishUsage);
        }

        /// <summary>
        /// Retrieves a buffer to be written to.
        /// </summary>
        /// <returns>The buffer.</returns>
        public Buffer GetForWrite()
        {
            Buffer usage = buffers[writeIndex];
            usage.LastUsage = UsageType.Write;
            return usage;
        }

        /// <summary>
        /// Attempts to retrieve a buffer to read.
        /// </summary>
        /// <param name="timeout">Amount of time (in milliseconds) to wait for a buffer to be written.</param>
        /// <returns>An available buffer to be read, or <c>null</c> if no buffer has been written.</returns>
        public Buffer? GetForRead(int timeout = DEFAULT_READ_TIMEOUT)
        {
            stopwatch.Restart();

            while (true)
            {
                // Reset before checking, so that a write finishing after the check still wakes the wait below.
                writeFinished.Reset();

                flip(ref readIndex);

                if (buffers[readIndex].LastUsage != UsageType.Read)
                    break;

                if (timeout == 0)
                    return null;

                int remaining = timeout - (int)stopwatch.ElapsedMilliseconds;

                if (remaining < 0)
                    return null;

                // Spinning here occupied a whole core whenever the reader outpaced the writer (e.g. the draw thread waiting on update).
                writeFinished.Wait(remaining);
            }

            Buffer usage = buffers[readIndex];

            Debug.Assert(usage.LastUsage == UsageType.Write);
            usage.LastUsage = UsageType.Read;

            return usage;
        }

        private void finishUsage(Buffer usage)
        {
            if (usage.LastUsage == UsageType.Write)
            {
                flip(ref writeIndex);
                writeFinished.Set();
            }
        }

        private void flip(ref int localIndex)
        {
            localIndex = Interlocked.Exchange(ref flipIndex, localIndex);
        }

        public class Buffer : IDisposable
        {
            public T? Object;

            public volatile UsageType LastUsage;

            public readonly int Index;

            private readonly Action<Buffer>? finish;

            public Buffer(int index, Action<Buffer>? finish)
            {
                Index = index;
                this.finish = finish;
            }

            public void Dispose()
            {
                finish?.Invoke(this);
            }
        }

        public enum UsageType
        {
            Read,
            Write
        }
    }
}
