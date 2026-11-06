// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// How a cell's frame time compares with the baseline's, round by round.
    /// </summary>
    /// <remarks>
    /// Machines drift during a matrix (a laptop leaving its boost window can slow every process by a third), so each round's median
    /// is compared with the baseline's median from the same round, and the change is the geometric mean of those ratios.
    /// Rounds alternate direction, which cancels a steady drift over each pair of rounds.
    /// </remarks>
    public sealed class BenchmarkComparison
    {
        /// <summary>
        /// Rounds needed before a change is reported as significant.
        /// </summary>
        public const int MINIMUM_SIGNIFICANT_ROUNDS = 3;

        /// <summary>
        /// Changes smaller than this fraction are not reported as significant, however consistent.
        /// </summary>
        public const double MINIMUM_SIGNIFICANT_CHANGE = 0.01;

        /// <summary>
        /// Two-sided 95% critical values of Student's t distribution, by degrees of freedom from one.
        /// </summary>
        private static readonly double[] critical_values =
        {
            12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
            2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        };

        /// <summary>
        /// The change of the median frame time, as a fraction.
        /// </summary>
        public double Change { get; }

        /// <summary>
        /// The 95% confidence interval of <see cref="Change"/>, or <c>null</c> with fewer than two rounds.
        /// </summary>
        public (double Lower, double Upper)? Interval { get; }

        /// <summary>
        /// The rounds that both cells have.
        /// </summary>
        public int Rounds { get; }

        /// <summary>
        /// Whether the interval excludes no change, with at least <see cref="MINIMUM_SIGNIFICANT_ROUNDS"/> rounds.
        /// </summary>
        public bool IsSignificant { get; }

        private BenchmarkComparison(IReadOnlyList<double> logRatios)
        {
            Rounds = logRatios.Count;

            double mean = logRatios.Average();
            Change = Math.Exp(mean) - 1;

            if (Rounds < 2)
                return;

            double deviation = Math.Sqrt(logRatios.Sum(r => (r - mean) * (r - mean)) / (Rounds - 1));
            double t = critical_values[Math.Min(Rounds - 2, critical_values.Length - 1)];
            double margin = t * deviation / Math.Sqrt(Rounds);

            Interval = (Math.Exp(mean - margin) - 1, Math.Exp(mean + margin) - 1);
            IsSignificant = Rounds >= MINIMUM_SIGNIFICANT_ROUNDS
                            && Math.Abs(Change) >= MINIMUM_SIGNIFICANT_CHANGE
                            && (Interval.Value.Lower > 0 || Interval.Value.Upper < 0);
        }

        /// <summary>
        /// Compares per-round medians, or returns <c>null</c> if the cells share no round.
        /// </summary>
        internal static BenchmarkComparison? Create(IReadOnlyDictionary<int, double> medians, IReadOnlyDictionary<int, double> baselineMedians)
        {
            double[] logRatios = medians.Where(m => baselineMedians.TryGetValue(m.Key, out double b) && b > 0 && m.Value > 0)
                                   .Select(m => Math.Log(m.Value / baselineMedians[m.Key]))
                                   .ToArray();

            return logRatios.Length == 0 ? null : new BenchmarkComparison(logRatios);
        }
    }
}
