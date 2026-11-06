// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// Formats matrix results as text.
    /// </summary>
    public static class BenchmarkReport
    {
        /// <summary>
        /// A Markdown table of median frame times, with each configuration's change from the baseline.
        /// Changes in bold are significant (see <see cref="BenchmarkComparison"/>).
        /// </summary>
        public static string CreateMarkdown(BenchmarkMatrixResult result)
        {
            var builder = new StringBuilder();

            builder.AppendLine(CultureInfo.InvariantCulture, $"# {result.Matrix}");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"{result.Device}, {result.Width}×{result.Height}, {result.MeasuredFrames} frames after {result.WarmupFrames} warm-up, {result.Rounds} rounds, {result.Started:yyyy-MM-dd HH:mm}.");
            builder.AppendLine("Median frame time (update + draw + GPU wait) in ms. Changes from the baseline are compared round by round; those in bold have a 95% interval that excludes no change.");
            builder.AppendLine();

            builder.AppendLine("| Case | " + string.Join(" | ", result.Configurations.Select(c => c.Name)) + " |");
            builder.AppendLine("| --- |" + string.Concat(Enumerable.Repeat(" ---: |", result.Configurations.Count)));

            foreach (var benchmarkCase in result.Cases)
            {
                var baseline = result.GetCell(benchmarkCase.Name, result.Baseline);

                builder.Append(CultureInfo.InvariantCulture, $"| {benchmarkCase.Name} |");

                foreach (var configuration in result.Configurations)
                {
                    var cell = result.GetCell(benchmarkCase.Name, configuration.Name);
                    builder.Append(CultureInfo.InvariantCulture, $" {FormatCell(cell, baseline)} |");
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        /// <summary>
        /// A cell's median frame time and its change from the baseline.
        /// </summary>
        public static string FormatCell(BenchmarkCell cell, BenchmarkCell baseline)
        {
            if (!cell.HasData)
                return cell.Error != null ? "failed" : "–";

            string time = FormatTime(cell.Median);

            if (cell.Configuration == baseline.Configuration || cell.ChangeFrom(baseline) is not double change)
                return time;

            string formatted = FormatChange(change);
            return cell.IsSignificantlyDifferentFrom(baseline) ? $"{time} **{formatted}**" : $"{time} {formatted}";
        }

        public static string FormatTime(double milliseconds) => milliseconds.ToString(milliseconds < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);

        public static string FormatChange(double change) => (change >= 0 ? "+" : "−") + Math.Abs(change * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
}
