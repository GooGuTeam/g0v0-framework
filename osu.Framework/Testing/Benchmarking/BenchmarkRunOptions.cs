// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// What part of a matrix to run, and how.
    /// </summary>
    public sealed class BenchmarkRunOptions
    {
        /// <summary>
        /// Where results are written. Earlier results there are replaced.
        /// </summary>
        public required string ResultsDirectory { get; init; }

        public BenchmarkDesign Design { get; init; } = BenchmarkDesign.OneFactorAtATime;

        /// <summary>
        /// The factors to vary, or <c>null</c> for those the matrix includes by default.
        /// </summary>
        public IReadOnlyList<string>? Factors { get; init; }

        /// <summary>
        /// The cases to run, or <c>null</c> for all of them.
        /// </summary>
        public IReadOnlyList<string>? Cases { get; init; }

        /// <summary>
        /// Overrides <see cref="BenchmarkMatrix.Rounds"/>.
        /// </summary>
        public int? Rounds { get; init; }

        /// <summary>
        /// Overrides <see cref="BenchmarkMatrix.WarmupFrames"/>.
        /// </summary>
        public int? WarmupFrames { get; init; }

        /// <summary>
        /// Overrides <see cref="BenchmarkMatrix.MeasuredFrames"/>.
        /// </summary>
        public int? MeasuredFrames { get; init; }
    }
}
