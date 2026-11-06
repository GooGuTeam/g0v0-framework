// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;

namespace osu.Framework.Testing.Benchmarking.Display
{
    /// <summary>
    /// One case under one configuration: median frame time, change from the baseline, and where the time goes.
    /// </summary>
    internal partial class MatrixCell : CompositeDrawable
    {
        public const float HEIGHT = 108;

        public readonly BindableBool Selected = new BindableBool();

        public Action? Action;

        private readonly Box background;

        /// <param name="cell">The cell.</param>
        /// <param name="baseline">The baseline cell of the same case.</param>
        /// <param name="pixels">How the capture differs from the baseline's, if compared.</param>
        /// <param name="rowMaximum">The slowest median frame time in the row, which scales the time bar.</param>
        /// <param name="pending">Whether runs that may fill the cell remain.</param>
        public MatrixCell(BenchmarkCell cell, BenchmarkCell baseline, BenchmarkPixelDifference? pixels, double rowMaximum, bool pending)
        {
            Height = HEIGHT;
            Masking = true;
            CornerRadius = 6;

            bool isBaseline = cell.Configuration == baseline.Configuration;
            double? change = isBaseline ? null : cell.ChangeFrom(baseline);
            bool significant = !isBaseline && cell.IsSignificantlyDifferentFrom(baseline);

            InternalChildren = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = isBaseline ? BenchmarkColours.PANEL_RAISED : BenchmarkColours.PANEL,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = change is double c ? BenchmarkColours.ForChange(c, significant) : Colour4.Transparent,
                    Alpha = significant ? 0.12f : 0,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 12, Top = 10, Bottom = 14 },
                    Child = cell.HasData ? createContent(cell, change, significant, isBaseline, pixels) : createPlaceholder(cell, pending),
                },
            };

            if (cell.HasData && rowMaximum > 0)
                AddInternal(createTimeBar(cell, rowMaximum));
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Selected.BindValueChanged(_ => updateState(), true);
        }

        protected override bool OnClick(ClickEvent e)
        {
            Action?.Invoke();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateState();
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e) => updateState();

        private void updateState()
        {
            BorderThickness = Selected.Value ? 2 : IsHovered ? 1 : 0;
            BorderColour = Selected.Value ? BenchmarkColours.ACCENT : BenchmarkColours.TEXT_FAINT;
            background.Alpha = IsHovered ? 0.8f : 1;
        }

        private static Drawable createContent(BenchmarkCell cell, double? change, bool significant, bool isBaseline, BenchmarkPixelDifference? pixels)
        {
            string changeText = isBaseline ? "baseline"
                : change is double c ? (significant ? string.Empty : "≈ ") + BenchmarkReport.FormatChange(c)
                : string.Empty;

            var details = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(10, 0),
                Children = new Drawable[]
                {
                    new SpriteText
                    {
                        Text = $"{FormatCount(cell.Counters.GetValueOrDefault(@"DrawCalls"))} draws · {FormatCount(cell.Counters.GetValueOrDefault(@"VerticesDraw"))} vertices",
                        Font = BenchmarkColours.Font(12, condensed: true),
                        Colour = BenchmarkColours.TEXT_FAINT,
                    },
                },
            };

            if (pixels != null)
            {
                bool differs = pixels.Error != null || pixels.DifferingFraction > 0.001;

                details.Add(new SpriteText
                {
                    Text = pixels.Error != null ? "pixels: not compared" : $"pixels {(pixels.DifferingFraction * 100).ToString("0.0#", CultureInfo.InvariantCulture)}%",
                    Font = BenchmarkColours.Font(12, bold: differs, condensed: true),
                    Colour = differs ? BenchmarkColours.WARNING : BenchmarkColours.TEXT_FAINT,
                });
            }

            return new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 5),
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Children = new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(3, 0),
                                Children = new Drawable[]
                                {
                                    new SpriteText
                                    {
                                        Text = BenchmarkReport.FormatTime(cell.Median),
                                        Font = BenchmarkColours.Font(24, bold: true),
                                        Colour = BenchmarkColours.TEXT,
                                    },
                                    new SpriteText
                                    {
                                        Anchor = Anchor.BottomLeft,
                                        Origin = Anchor.BottomLeft,
                                        Margin = new MarginPadding { Bottom = 4 },
                                        Text = "ms",
                                        Font = BenchmarkColours.Font(13),
                                        Colour = BenchmarkColours.TEXT_DIM,
                                    },
                                },
                            },
                            new SpriteText
                            {
                                Anchor = Anchor.TopRight,
                                Origin = Anchor.TopRight,
                                Margin = new MarginPadding { Top = 4 },
                                Text = changeText,
                                Font = BenchmarkColours.Font(isBaseline ? 13 : 16, bold: significant),
                                Colour = change is double c2 ? BenchmarkColours.ForChange(c2, significant) : BenchmarkColours.TEXT_FAINT,
                            },
                        },
                    },
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new Vector2(8, 0),
                        Children = new Drawable[]
                        {
                            breakdown("U", cell.Update, BenchmarkColours.UPDATE),
                            breakdown("D", cell.Draw, BenchmarkColours.DRAW),
                            breakdown("G", cell.Gpu, BenchmarkColours.GPU),
                            new SpriteText
                            {
                                Text = $"p95 {BenchmarkReport.FormatTime(cell.P95)}",
                                Font = BenchmarkColours.Font(12, condensed: true),
                                Colour = BenchmarkColours.TEXT_DIM,
                            },
                        },
                    },
                    details,
                },
            };
        }

        private static Drawable breakdown(string label, double value, Colour4 colour) => new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(3, 0),
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Text = label,
                    Font = BenchmarkColours.Font(12, bold: true, condensed: true),
                    Colour = colour,
                },
                new SpriteText
                {
                    Text = BenchmarkReport.FormatTime(value),
                    Font = BenchmarkColours.Font(12, condensed: true),
                    Colour = BenchmarkColours.TEXT_DIM,
                },
            },
        };

        private static Drawable createPlaceholder(BenchmarkCell cell, bool pending)
        {
            string firstLine = cell.Error?.Split('\n').FirstOrDefault() ?? string.Empty;

            return new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 4),
                Children = new Drawable[]
                {
                    new SpriteText
                    {
                        Text = cell.Error != null ? "failed" : pending ? "…" : "–",
                        Font = BenchmarkColours.Font(20, bold: true),
                        Colour = cell.Error != null ? BenchmarkColours.SLOWER : BenchmarkColours.TEXT_FAINT,
                    },
                    new TextFlowContainer(t =>
                    {
                        t.Font = BenchmarkColours.Font(12, condensed: true);
                        t.Colour = BenchmarkColours.TEXT_DIM;
                    })
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Text = firstLine.Length > 140 ? firstLine[..140] + "…" : firstLine,
                    },
                },
            };
        }

        /// <summary>
        /// Update, draw and GPU time as one bar, scaled to the slowest cell of the row.
        /// </summary>
        private static Drawable createTimeBar(BenchmarkCell cell, double rowMaximum)
        {
            float scale = (float)(1 / rowMaximum);

            return new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 4,
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                Padding = new MarginPadding { Horizontal = 12 },
                Margin = new MarginPadding { Bottom = 6 },
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Width = (float)cell.Update * scale, Colour = BenchmarkColours.UPDATE },
                        new Box { RelativeSizeAxes = Axes.Both, Width = (float)cell.Draw * scale, Colour = BenchmarkColours.DRAW },
                        new Box { RelativeSizeAxes = Axes.Both, Width = (float)cell.Gpu * scale, Colour = BenchmarkColours.GPU },
                    },
                },
            };
        }

        public static string FormatCount(double value)
            => value >= 1_000_000 ? (value / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M"
                : value >= 10_000 ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                : value.ToString("0", CultureInfo.InvariantCulture);
    }
}
