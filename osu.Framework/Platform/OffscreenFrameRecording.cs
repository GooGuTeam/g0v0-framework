// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Statistics;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Per-frame timings recorded by an <see cref="OffscreenGameHost"/>.
    /// </summary>
    public sealed class OffscreenFrameRecording
    {
        /// <summary>
        /// The number of frames.
        /// </summary>
        public readonly int Frames;

        /// <summary>
        /// The time spent updating the scene and building its draw nodes, in milliseconds.
        /// </summary>
        public readonly double[] UpdateTimes;

        /// <summary>
        /// The CPU time spent submitting the frame to the GPU, in milliseconds.
        /// </summary>
        public readonly double[] DrawTimes;

        /// <summary>
        /// The time spent waiting for the GPU after submission, in milliseconds.
        /// </summary>
        public readonly double[] GpuTimes;

        /// <summary>
        /// The managed memory allocated by the process during the frame, in bytes.
        /// </summary>
        public readonly long[] AllocatedBytes;

        /// <summary>
        /// The names of the draw counters, in the order of <see cref="Counters"/>.
        /// </summary>
        public readonly string[] CounterNames;

        /// <summary>
        /// Draw counters for each frame, such as draw calls and vertices.
        /// </summary>
        public readonly long[][] Counters;

        /// <summary>
        /// The garbage collections of each generation during the recording.
        /// </summary>
        public readonly int[] Collections = new int[3];

        internal OffscreenFrameRecording(int frames, IReadOnlyList<StatisticsCounterType> counters)
        {
            Frames = frames;
            UpdateTimes = new double[frames];
            DrawTimes = new double[frames];
            GpuTimes = new double[frames];
            AllocatedBytes = new long[frames];

            CounterNames = new string[counters.Count];
            Counters = new long[counters.Count][];

            for (int i = 0; i < counters.Count; i++)
            {
                CounterNames[i] = counters[i].ToString();
                Counters[i] = new long[frames];
            }
        }

        internal void Add(int frame, double update, double draw, double gpu, long allocated)
        {
            UpdateTimes[frame] = update;
            DrawTimes[frame] = draw;
            GpuTimes[frame] = gpu;
            AllocatedBytes[frame] = allocated;
        }
    }
}
