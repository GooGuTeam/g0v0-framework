// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace osu.Framework.Testing.Benchmarking.Display
{
    /// <summary>
    /// Frame times of a configuration beside the baseline's, in order. Each bucket of frames shows its range and median.
    /// </summary>
    internal partial class FrameTimeChart : CompositeDrawable
    {
        private const int buckets = 100;
        private const float axis_width = 46;
        private const float axis_height = 18;

        private readonly Container axis;
        private readonly Container plot;
        private readonly SpriteText placeholder;
        private readonly FillFlowContainer legend;

        public FrameTimeChart()
        {
            InternalChildren = new Drawable[]
            {
                axis = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = axis_width, Bottom = axis_height },
                },
                plot = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = axis_width, Bottom = axis_height },
                },
                placeholder = new SpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Font = BenchmarkColours.Font(13),
                    Colour = BenchmarkColours.TEXT_FAINT,
                },
                new SpriteText
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    Text = "frames in order; each bar is the range of a bucket, each tick its median",
                    Font = BenchmarkColours.Font(11, condensed: true),
                    Colour = BenchmarkColours.TEXT_FAINT,
                },
                legend = new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.BottomRight,
                    Direction = FillDirection.Horizontal,
                    Spacing = new osuTK.Vector2(14, 0),
                    Y = -4,
                },
            };
        }

        private static Drawable legendEntry(string name, Colour4 colour) => new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new osuTK.Vector2(5, 0),
            Children = new Drawable[]
            {
                new Box
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new osuTK.Vector2(10, 3),
                    Colour = colour,
                },
                new SpriteText
                {
                    Text = name,
                    Font = BenchmarkColours.Font(12, bold: true, condensed: true),
                    Colour = colour,
                },
            },
        };

        /// <param name="baselineName">The baseline's name, for the legend.</param>
        /// <param name="baseline">The baseline's frame times, or <c>null</c>.</param>
        /// <param name="name">The selected configuration's name, for the legend.</param>
        /// <param name="values">The selected configuration's frame times, or <c>null</c> to show the baseline alone.</param>
        public void Show(string baselineName, double[]? baseline, string name, double[]? values)
        {
            axis.Clear();
            plot.Clear();
            legend.Clear();

            if (baseline is { Length: > 0 })
                legend.Add(legendEntry(baselineName, BenchmarkColours.TEXT_DIM));
            if (values is { Length: > 0 })
                legend.Add(legendEntry(name, BenchmarkColours.ACCENT));

            double[][] series = new[] { baseline, values }.Where(s => s is { Length: > 0 }).Select(s => s!).ToArray();

            if (series.Length == 0)
            {
                placeholder.Text = "No frames";
                return;
            }

            placeholder.Text = string.Empty;

            // Scale to the slowest 99th percentile, so that single spikes do not flatten everything else; they are clipped.
            double top = niceCeiling(series.Max(s => BenchmarkCell.Percentile(s, 0.99)) * 1.2);

            for (int i = 0; i <= 4; i++)
            {
                float y = 1 - i / 4f;

                axis.Add(new Box
                {
                    RelativeSizeAxes = Axes.X,
                    RelativePositionAxes = Axes.Y,
                    Y = y,
                    Height = 1,
                    Colour = BenchmarkColours.BORDER,
                });

                axis.Add(new SpriteText
                {
                    RelativePositionAxes = Axes.Y,
                    Y = y,
                    X = -6,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.CentreRight,
                    Text = $"{BenchmarkReport.FormatTime(top * i / 4)} ms",
                    Font = BenchmarkColours.Font(11),
                    Colour = BenchmarkColours.TEXT_FAINT,
                });
            }

            bool comparing = baseline is { Length: > 0 } && values is { Length: > 0 };

            if (baseline is { Length: > 0 })
                addSeries(baseline, top, BenchmarkColours.TEXT_DIM, comparing ? 0 : 0.25f, comparing ? 0.45f : 0.5f);
            if (values is { Length: > 0 })
                addSeries(values, top, BenchmarkColours.ACCENT, comparing ? 0.5f : 0.25f, comparing ? 0.45f : 0.5f);
        }

        private void addSeries(double[] values, double top, Colour4 colour, float offset, float width)
        {
            int count = Math.Min(buckets, values.Length);

            for (int b = 0; b < count; b++)
            {
                double[] bucket = values[(b * values.Length / count)..((b + 1) * values.Length / count)];

                float low = (float)Math.Min(bucket.Min() / top, 1);
                float high = (float)Math.Min(bucket.Max() / top, 1);
                float median = (float)Math.Min(BenchmarkCell.Percentile(bucket, 0.5) / top, 1);
                float x = (b + offset) / count;

                plot.Add(new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    RelativePositionAxes = Axes.Both,
                    X = x,
                    Y = 1 - high,
                    Width = width / count,
                    Height = Math.Max(high - low, 0.004f),
                    Colour = colour,
                    Alpha = 0.3f,
                });

                plot.Add(new Box
                {
                    RelativeSizeAxes = Axes.X,
                    RelativePositionAxes = Axes.Both,
                    Origin = Anchor.CentreLeft,
                    X = x,
                    Y = 1 - median,
                    Width = width / count,
                    Height = 2,
                    Colour = colour,
                });
            }
        }

        /// <summary>
        /// The smallest round step times a power of ten at or above <paramref name="value"/>.
        /// </summary>
        private static double niceCeiling(double value)
        {
            if (value <= 0)
                return 1;

            double power = Math.Pow(10, Math.Floor(Math.Log10(value)));

            foreach (double step in new[] { 1.0, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            {
                if (step * power >= value)
                    return step * power;
            }

            return 10 * power;
        }
    }
}
