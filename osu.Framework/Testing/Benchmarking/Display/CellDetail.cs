// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace osu.Framework.Testing.Benchmarking.Display
{
    /// <summary>
    /// The selected cell beside the baseline: captured frames, frame times and every statistic.
    /// </summary>
    internal partial class CellDetail : CompositeDrawable
    {
        private static readonly (string Counter, string Label)[] counters =
        {
            (@"DrawCalls", "Draw calls"),
            (@"VerticesDraw", "Vertices"),
            (@"TextureBinds", "Texture binds"),
            (@"ShaderBinds", "Shader binds"),
            (@"FBORedraw", "Framebuffer redraws"),
            (@"VBufBinds", "Vertex buffer binds"),
            (@"UniformUpl", "Uniform uploads"),
        };

        private readonly SpriteText title;
        private readonly SpriteText subtitle;
        private readonly CaptureView baselineCapture;
        private readonly CaptureView capture;
        private readonly FrameTimeChart chart;
        private readonly FillFlowContainer statistics;

        public bool IsReady => !baselineCapture.IsLoading && !capture.IsLoading;

        public CellDetail()
        {
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = BenchmarkColours.PANEL,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Colour = BenchmarkColours.BORDER,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 24, Vertical = 16 },
                    Child = new GridContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        // A fixed size: the title arrives after the first layout, which would not resize an auto-sized row.
                        RowDimensions = new[] { new Dimension(GridSizeMode.Absolute, 26), new Dimension(GridSizeMode.Absolute, 10), new Dimension() },
                        Content = new[]
                        {
                            new Drawable[]
                            {
                                new FillFlowContainer
                                {
                                    AutoSizeAxes = Axes.Both,
                                    Direction = FillDirection.Horizontal,
                                    Spacing = new Vector2(12, 0),
                                    Children = new Drawable[]
                                    {
                                        title = new SpriteText
                                        {
                                            Font = BenchmarkColours.Font(20, bold: true),
                                            Colour = BenchmarkColours.TEXT,
                                        },
                                        subtitle = new SpriteText
                                        {
                                            Anchor = Anchor.BottomLeft,
                                            Origin = Anchor.BottomLeft,
                                            Margin = new MarginPadding { Bottom = 2 },
                                            Font = BenchmarkColours.Font(14),
                                            Colour = BenchmarkColours.TEXT_DIM,
                                        },
                                    },
                                },
                            },
                            Array.Empty<Drawable>(),
                            new Drawable[]
                            {
                                new GridContainer
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    ColumnDimensions = new[]
                                    {
                                        new Dimension(GridSizeMode.Relative, 0.22f),
                                        new Dimension(GridSizeMode.Absolute, 12),
                                        new Dimension(GridSizeMode.Relative, 0.22f),
                                        new Dimension(GridSizeMode.Absolute, 28),
                                        new Dimension(),
                                        new Dimension(GridSizeMode.Absolute, 28),
                                        new Dimension(GridSizeMode.Absolute, 420),
                                    },
                                    Content = new[]
                                    {
                                        new Drawable?[]
                                        {
                                            baselineCapture = new CaptureView { RelativeSizeAxes = Axes.Both },
                                            null,
                                            capture = new CaptureView { RelativeSizeAxes = Axes.Both },
                                            null,
                                            chart = new FrameTimeChart { RelativeSizeAxes = Axes.Both },
                                            null,
                                            new BasicScrollContainer
                                            {
                                                RelativeSizeAxes = Axes.Both,
                                                ScrollbarVisible = false,
                                                Child = statistics = new FillFlowContainer
                                                {
                                                    RelativeSizeAxes = Axes.X,
                                                    AutoSizeAxes = Axes.Y,
                                                    Direction = FillDirection.Vertical,
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };
        }

        public void Show(BenchmarkMatrixResult result, string resultsDirectory, string caseName, string configuration)
        {
            var cell = result.GetCell(caseName, configuration);
            var baseline = result.GetCell(caseName, result.Baseline);
            bool isBaseline = configuration == result.Baseline;

            title.Text = caseName;
            subtitle.Text = isBaseline ? $"{configuration} (baseline)" : $"{configuration} against {result.Baseline}";

            baselineCapture.Show(result.Baseline, capturePath(baseline));
            capture.Show(isBaseline ? string.Empty : configuration, isBaseline ? null : capturePath(cell));
            capture.Alpha = isBaseline ? 0 : 1;

            chart.Show(result.Baseline, representative(baseline)?.GetTotals(), configuration, isBaseline ? null : representative(cell)?.GetTotals());

            statistics.Clear();
            statistics.Add(header(isBaseline));

            if (!cell.HasData)
            {
                statistics.Add(new TextFlowContainer(t =>
                {
                    t.Font = BenchmarkColours.Font(13);
                    t.Colour = cell.Error != null ? BenchmarkColours.SLOWER : BenchmarkColours.TEXT_DIM;
                })
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Margin = new MarginPadding { Top = 8 },
                    Text = cell.Error ?? "No results yet.",
                });
                return;
            }

            var comparison = isBaseline ? null : cell.CompareTo(baseline);

            // The change of the median is compared round by round, as in the table, so it can differ from the ratio of the medians shown.
            add("Frame time (median)", baseline.Median, cell.Median, time, comparison?.IsSignificant ?? false, comparison?.Change);

            if (comparison?.Interval is { } interval)
            {
                statistics.Add(row($"95% interval ({comparison.Rounds} rounds)", string.Empty, string.Empty,
                    $"{BenchmarkReport.FormatChange(interval.Lower)} to {BenchmarkReport.FormatChange(interval.Upper)}",
                    BenchmarkColours.ForChange(comparison.Change, comparison.IsSignificant)));
            }

            add("Range over rounds", baseline.High - baseline.Low, cell.High - cell.Low, time, null);
            add("95th percentile", baseline.P95, cell.P95, time, null);
            add("99th percentile", baseline.P99, cell.P99, time, null);
            add("Update", baseline.Update, cell.Update, time, null);
            add("Draw", baseline.Draw, cell.Draw, time, null);
            add("GPU wait", baseline.Gpu, cell.Gpu, time, null);
            add("Allocated per frame", baseline.AllocatedBytes, cell.AllocatedBytes, v => $"{(v / 1024).ToString("0.0", CultureInfo.InvariantCulture)} KB", null);
            add("Collections per round", baseline.Collections, cell.Collections, v => v.ToString("0.#", CultureInfo.InvariantCulture), null);
            add("Real time per run", baseline.Seconds, cell.Seconds, v => $"{v.ToString("0.0", CultureInfo.InvariantCulture)} s", null);

            foreach (var (counter, label) in counters)
                add(label, baseline.Counters.GetValueOrDefault(counter), cell.Counters.GetValueOrDefault(counter), MatrixCell.FormatCount, null);

            if (result.GetPixelDifference(caseName, configuration) is BenchmarkPixelDifference pixels && pixels.Error == null)
            {
                statistics.Add(row("Pixels differing", string.Empty, $"{(pixels.DifferingFraction * 100).ToString("0.###", CultureInfo.InvariantCulture)}%", string.Empty,
                    pixels.DifferingFraction > 0.001 ? BenchmarkColours.WARNING : BenchmarkColours.TEXT_DIM));
                statistics.Add(row("Largest channel difference", string.Empty, $"{pixels.MaximumDifference}/255", string.Empty, BenchmarkColours.TEXT_DIM));
            }

            void add(string label, double baselineValue, double value, Func<double, string> format, bool? isSignificant, double? pairedChange = null)
            {
                if (isBaseline)
                {
                    statistics.Add(row(label, format(value), string.Empty, string.Empty, BenchmarkColours.TEXT_DIM));
                    return;
                }

                double change = pairedChange ?? (baselineValue != 0 ? value / baselineValue - 1 : value == 0 ? 0 : double.PositiveInfinity);

                // Timings vary between rounds, so only the median's change has a significance test; for the rest, small changes are dimmed.
                bool highlight = isSignificant ?? Math.Abs(change) >= 0.03;

                statistics.Add(row(label, format(baselineValue), format(value),
                    double.IsInfinity(change) ? "new" : Math.Abs(change) < 0.0005 ? "=" : BenchmarkReport.FormatChange(change),
                    BenchmarkColours.ForChange(change, highlight)));
            }

            string? capturePath(BenchmarkCell c)
            {
                string? relative = result.Runs.FirstOrDefault(r => r.Configuration == c.Configuration && r.Round == BenchmarkRun.PRIMING_ROUND)?.Cases.FirstOrDefault(r => r.Case == caseName)?.Capture;
                return relative == null ? null : Path.Combine(resultsDirectory, relative);
            }
        }

        /// <summary>
        /// The run whose median is closest to the cell's.
        /// </summary>
        private static BenchmarkCaseRun? representative(BenchmarkCell cell)
            => cell.Runs.OrderBy(r => Math.Abs(BenchmarkCell.Percentile(r.GetTotals(), 0.5) - cell.Median)).FirstOrDefault();

        private static string time(double milliseconds) => $"{BenchmarkReport.FormatTime(milliseconds)} ms";

        private static Drawable header(bool isBaseline) => row(string.Empty, "Baseline", isBaseline ? string.Empty : "This", isBaseline ? string.Empty : "Change",
            BenchmarkColours.TEXT_FAINT, bold: true);

        private static Drawable row(string label, string baseline, string value, string change, Colour4 changeColour, bool bold = false)
        {
            var font = BenchmarkColours.Font(13, bold: bold, condensed: true);

            return new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 19,
                Children = new Drawable[]
                {
                    new SpriteText { Text = label, Font = font, Colour = BenchmarkColours.TEXT_DIM },
                    new SpriteText { RelativePositionAxes = Axes.X, X = 0.62f, Origin = Anchor.TopRight, Text = baseline, Font = font, Colour = BenchmarkColours.TEXT_DIM },
                    new SpriteText { RelativePositionAxes = Axes.X, X = 0.82f, Origin = Anchor.TopRight, Text = value, Font = font, Colour = BenchmarkColours.TEXT },
                    new SpriteText { RelativePositionAxes = Axes.X, X = 1, Origin = Anchor.TopRight, Text = change, Font = font, Colour = changeColour },
                },
            };
        }
    }
}
