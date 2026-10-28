// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// The runs of one case under one configuration, summarised.
    /// Each figure is the median over rounds of a per-round statistic, so one disturbed round does not move it.
    /// Changes from the baseline are compared round by round instead (see <see cref="BenchmarkComparison"/>).
    /// </summary>
    public sealed class BenchmarkCell
    {
        public string Case { get; }

        public string Configuration { get; }

        /// <summary>
        /// Successful runs, by round.
        /// </summary>
        public IReadOnlyList<BenchmarkCaseRun> Runs { get; }

        /// <summary>
        /// The median frame time of each successful round, in milliseconds.
        /// </summary>
        public IReadOnlyDictionary<int, double> RoundMedians { get; }

        /// <summary>
        /// The first error, if any run failed.
        /// </summary>
        public string? Error { get; }

        public bool HasData => Runs.Count > 0;

        /// <summary>
        /// Median frame time, in milliseconds.
        /// </summary>
        public double Median { get; }

        /// <summary>
        /// The lowest and highest per-round median, in milliseconds.
        /// </summary>
        public double Low { get; }

        /// <inheritdoc cref="Low"/>
        public double High { get; }

        /// <summary>
        /// 95th percentile frame time, in milliseconds.
        /// </summary>
        public double P95 { get; }

        /// <summary>
        /// 99th percentile frame time, in milliseconds.
        /// </summary>
        public double P99 { get; }

        /// <summary>
        /// Median update time, in milliseconds.
        /// </summary>
        public double Update { get; }

        /// <summary>
        /// Median draw submission time, in milliseconds.
        /// </summary>
        public double Draw { get; }

        /// <summary>
        /// Median GPU wait, in milliseconds.
        /// </summary>
        public double Gpu { get; }

        /// <summary>
        /// Mean managed allocation per frame, in bytes.
        /// </summary>
        public double AllocatedBytes { get; }

        /// <summary>
        /// Garbage collections of any generation per round.
        /// </summary>
        public double Collections { get; }

        /// <summary>
        /// Mean draw counters per frame.
        /// </summary>
        public IReadOnlyDictionary<string, double> Counters { get; }

        /// <summary>
        /// Mean real time each run of the case took, in seconds.
        /// </summary>
        public double Seconds { get; }

        /// <param name="caseName">The case.</param>
        /// <param name="configuration">The configuration.</param>
        /// <param name="runs">The case's results from each measured round.</param>
        public BenchmarkCell(string caseName, string configuration, IReadOnlyList<(int Round, BenchmarkCaseRun Run)> runs)
        {
            Case = caseName;
            Configuration = configuration;
            Error = runs.Select(r => r.Run.Error).FirstOrDefault(e => e != null);

            var successful = runs.Where(r => r.Run.Error == null && r.Run.Update.Length > 0).ToArray();

            Runs = successful.Select(r => r.Run).ToArray();
            RoundMedians = successful.ToDictionary(r => r.Round, r => Percentile(r.Run.GetTotals(), 0.5));
            Counters = new Dictionary<string, double>();

            if (Runs.Count == 0)
                return;

            double[] medians = RoundMedians.Values.ToArray();

            Median = Percentile(medians, 0.5);
            Low = medians.Min();
            High = medians.Max();
            P95 = Percentile(Runs.Select(r => Percentile(r.GetTotals(), 0.95)).ToArray(), 0.5);
            P99 = Percentile(Runs.Select(r => Percentile(r.GetTotals(), 0.99)).ToArray(), 0.5);
            Update = Percentile(Runs.Select(r => Percentile(r.Update, 0.5)).ToArray(), 0.5);
            Draw = Percentile(Runs.Select(r => Percentile(r.Draw, 0.5)).ToArray(), 0.5);
            Gpu = Percentile(Runs.Select(r => Percentile(r.Gpu, 0.5)).ToArray(), 0.5);
            AllocatedBytes = Runs.Average(r => r.AllocatedBytes);
            Collections = Runs.Average(r => r.Collections.Sum());
            Seconds = Runs.Average(r => r.Seconds);
            Counters = Runs.SelectMany(r => r.Counters).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Average(c => c.Value));
        }

        /// <summary>
        /// Compares the median frame time with <paramref name="baseline"/>'s, round by round.
        /// </summary>
        /// <returns>The comparison, or <c>null</c> if the cells share no round.</returns>
        public BenchmarkComparison? CompareTo(BenchmarkCell baseline) => BenchmarkComparison.Create(RoundMedians, baseline.RoundMedians);

        /// <summary>
        /// The change of the median frame time from <paramref name="baseline"/>, as a fraction (see <see cref="CompareTo"/>).
        /// </summary>
        public double? ChangeFrom(BenchmarkCell baseline) => CompareTo(baseline)?.Change;

        /// <summary>
        /// Whether the median frame time changed from <paramref name="baseline"/>'s with 95% confidence (see <see cref="CompareTo"/>).
        /// </summary>
        public bool IsSignificantlyDifferentFrom(BenchmarkCell baseline) => CompareTo(baseline)?.IsSignificant == true;

        /// <summary>
        /// The value below which a fraction <paramref name="p"/> of <paramref name="values"/> lie, interpolating between neighbours.
        /// </summary>
        public static double Percentile(IReadOnlyList<double> values, double p)
        {
            if (values.Count == 0)
                return 0;

            double[] sorted = values.OrderBy(v => v).ToArray();
            double position = p * (sorted.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(lower + 1, sorted.Length - 1);

            return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }
    }
}
