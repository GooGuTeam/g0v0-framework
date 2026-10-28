// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Platform;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// The measured frames of one case in one run.
    /// </summary>
    public sealed class BenchmarkCaseRun
    {
        public string Case { get; set; } = string.Empty;

        /// <summary>
        /// Why the case failed, if it did.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Frames from the start of the case until its scene was loaded and set up.
        /// </summary>
        public int SetupFrames { get; set; }

        /// <summary>
        /// Real time the case took, including loading, holding and capturing.
        /// </summary>
        public double Seconds { get; set; }

        /// <summary>
        /// Update time of each frame, in milliseconds.
        /// </summary>
        public double[] Update { get; set; } = Array.Empty<double>();

        /// <summary>
        /// Draw submission time of each frame, in milliseconds.
        /// </summary>
        public double[] Draw { get; set; } = Array.Empty<double>();

        /// <summary>
        /// GPU wait of each frame, in milliseconds.
        /// </summary>
        public double[] Gpu { get; set; } = Array.Empty<double>();

        /// <summary>
        /// Mean managed allocation per frame, in bytes.
        /// </summary>
        public double AllocatedBytes { get; set; }

        /// <summary>
        /// Garbage collections of each generation while measuring.
        /// </summary>
        public int[] Collections { get; set; } = Array.Empty<int>();

        /// <summary>
        /// Mean draw counters per frame, such as draw calls.
        /// </summary>
        public Dictionary<string, double> Counters { get; set; } = new Dictionary<string, double>();

        /// <summary>
        /// The last measured frame as a PNG, relative to the results directory.
        /// </summary>
        public string? Capture { get; set; }

        /// <summary>
        /// The time of each frame, in milliseconds.
        /// </summary>
        public double[] GetTotals() => Update.Select((u, i) => u + Draw[i] + Gpu[i]).ToArray();

        internal static BenchmarkCaseRun FromRecording(string caseName, int setupFrames, OffscreenFrameRecording recording)
        {
            var run = new BenchmarkCaseRun
            {
                Case = caseName,
                SetupFrames = setupFrames,
                Update = round(recording.UpdateTimes),
                Draw = round(recording.DrawTimes),
                Gpu = round(recording.GpuTimes),
                AllocatedBytes = recording.AllocatedBytes.Average(),
                Collections = recording.Collections.ToArray(),
            };

            for (int i = 0; i < recording.CounterNames.Length; i++)
                run.Counters[recording.CounterNames[i]] = recording.Counters[i].Average();

            return run;

            // Sub-microsecond digits are noise and would only bloat the results.
            static double[] round(double[] values) => values.Select(v => Math.Round(v, 4)).ToArray();
        }
    }
}
