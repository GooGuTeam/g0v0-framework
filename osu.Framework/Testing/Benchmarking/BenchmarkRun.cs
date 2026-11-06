// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// The results of one benchmark process: every case under one configuration.
    /// </summary>
    public sealed class BenchmarkRun
    {
        /// <summary>
        /// The round of the run before the measured ones, which fills shader caches and captures each case instead of timing it.
        /// </summary>
        public const int PRIMING_ROUND = -1;

        public string Configuration { get; set; } = string.Empty;

        /// <summary>
        /// The measured round, from zero, or <see cref="PRIMING_ROUND"/>.
        /// </summary>
        public int Round { get; set; }

        /// <summary>
        /// The graphics backend and adapter.
        /// </summary>
        public string Device { get; set; } = string.Empty;

        public DateTimeOffset Started { get; set; }

        /// <summary>
        /// Why the process failed, if it did. Cases finished before the failure are kept.
        /// </summary>
        public string? Error { get; set; }

        public List<BenchmarkCaseRun> Cases { get; set; } = new List<BenchmarkCaseRun>();
    }
}
