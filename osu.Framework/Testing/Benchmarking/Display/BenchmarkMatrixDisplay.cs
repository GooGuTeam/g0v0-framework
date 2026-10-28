// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osuTK;

namespace osu.Framework.Testing.Benchmarking.Display
{
    /// <summary>
    /// Shows the latest results of a matrix as a table of cases against configurations, and starts new runs.
    /// </summary>
    public partial class BenchmarkMatrixDisplay : CompositeDrawable
    {
        private const float case_column_width = 270;
        private const float spacing = 8;
        private const float header_row_height = 58;
        private const float header_height = 180;

        private readonly BenchmarkMatrix matrix;
        private readonly BenchmarkRunOptions options;
        private readonly Func<IEnumerable<string>, ProcessStartInfo> createProcess;

        private readonly Bindable<BenchmarkDesign> design = new Bindable<BenchmarkDesign>();
        private readonly BindableInt rounds = new BindableInt { MinValue = 1, MaxValue = 9 };
        private readonly Dictionary<BenchmarkFactor, FactorChip> factorChips = new Dictionary<BenchmarkFactor, FactorChip>();
        private readonly Dictionary<(string Case, string Configuration), MatrixCell> cells = new Dictionary<(string, string), MatrixCell>();

        private SpriteText info = null!;
        private SpriteText status = null!;
        private Box progress = null!;
        private DisplayButton runButton = null!;
        private DisplayButton designButton = null!;
        private SpriteText roundsText = null!;
        private FillFlowContainer table = null!;
        private CellDetail detail = null!;

        [Resolved]
        private GameHost host { get; set; } = null!;

        private BenchmarkMatrixResult? result;
        private bool resultLoaded;
        private string? selectedCase;
        private string? selectedConfiguration;

        private BenchmarkMatrixRunner? runner;
        private CancellationTokenSource? cancellation;
        private (double Update, double Draw)? throttledFrom;

        /// <summary>
        /// Whether saved results and the selected cell's captures have finished loading.
        /// </summary>
        public bool IsReady => IsLoaded && resultLoaded && detail.IsReady;

        /// <param name="matrix">The matrix.</param>
        /// <param name="options">The results directory, and the initial choices for new runs.</param>
        /// <param name="createProcess">Starts this program with the given arguments; see <see cref="BenchmarkMatrixRunner"/>.</param>
        public BenchmarkMatrixDisplay(BenchmarkMatrix matrix, BenchmarkRunOptions options, Func<IEnumerable<string>, ProcessStartInfo> createProcess)
        {
            this.matrix = matrix;
            this.options = options;
            this.createProcess = createProcess;

            RelativeSizeAxes = Axes.Both;
            design.Value = options.Design;
            rounds.Value = options.Rounds ?? matrix.Rounds;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            var chips = new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(8),
            };

            foreach (var factor in matrix.Factors)
            {
                var chip = factorChips[factor] = new FactorChip(factor)
                {
                    Included = { Value = options.Factors?.Contains(factor.Name) ?? factor.IncludedByDefault },
                };

                chip.Included.BindValueChanged(_ => showResult());
                chips.Add(chip);
            }

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = BenchmarkColours.BACKGROUND,
                },
                new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    // Fixed sizes: text that arrives after the first layout would not resize an auto-sized row.
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Absolute, header_height),
                        new Dimension(),
                        new Dimension(GridSizeMode.Relative, 0.36f),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Padding = new MarginPadding { Horizontal = 24, Top = 18, Bottom = 14 },
                                Spacing = new Vector2(0, 10),
                                Children = new Drawable[]
                                {
                                    new FillFlowContainer
                                    {
                                        AutoSizeAxes = Axes.Both,
                                        Direction = FillDirection.Horizontal,
                                        Spacing = new Vector2(12, 0),
                                        Children = new Drawable[]
                                        {
                                            new SpriteText
                                            {
                                                Text = "Benchmark matrix",
                                                Font = BenchmarkColours.Font(30, bold: true),
                                                Colour = BenchmarkColours.TEXT,
                                            },
                                            new SpriteText
                                            {
                                                Anchor = Anchor.BottomLeft,
                                                Origin = Anchor.BottomLeft,
                                                Margin = new MarginPadding { Bottom = 3 },
                                                Text = matrix.Name,
                                                Font = BenchmarkColours.Font(22),
                                                Colour = BenchmarkColours.ACCENT,
                                            },
                                        },
                                    },
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 17,
                                        Child = info = new SpriteText
                                        {
                                            Font = BenchmarkColours.Font(14),
                                            Colour = BenchmarkColours.TEXT_DIM,
                                        },
                                    },
                                    new FillFlowContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Horizontal,
                                        Spacing = new Vector2(10),
                                        Children = new Drawable[]
                                        {
                                            runButton = new DisplayButton
                                            {
                                                Action = toggleRun,
                                            },
                                            designButton = new DisplayButton
                                            {
                                                Action = () => design.Value = design.Value == BenchmarkDesign.OneFactorAtATime ? BenchmarkDesign.Factorial : BenchmarkDesign.OneFactorAtATime,
                                            },
                                            new FillFlowContainer
                                            {
                                                AutoSizeAxes = Axes.Both,
                                                Direction = FillDirection.Horizontal,
                                                Spacing = new Vector2(4, 0),
                                                Children = new Drawable[]
                                                {
                                                    new DisplayButton { Text = "−", Action = () => rounds.Value-- },
                                                    roundsText = new SpriteText
                                                    {
                                                        Anchor = Anchor.CentreLeft,
                                                        Origin = Anchor.CentreLeft,
                                                        Margin = new MarginPadding { Horizontal = 6 },
                                                        Font = BenchmarkColours.Font(15, bold: true),
                                                        Colour = BenchmarkColours.TEXT,
                                                    },
                                                    new DisplayButton { Text = "+", Action = () => rounds.Value++ },
                                                },
                                            },
                                            new Container { Width = 12, Height = 34 },
                                            new Container
                                            {
                                                AutoSizeAxes = Axes.X,
                                                Height = 34,
                                                Child = new SpriteText
                                                {
                                                    Anchor = Anchor.CentreLeft,
                                                    Origin = Anchor.CentreLeft,
                                                    Text = "Vary",
                                                    Font = BenchmarkColours.Font(14),
                                                    Colour = BenchmarkColours.TEXT_DIM,
                                                },
                                            },
                                            new Container
                                            {
                                                AutoSizeAxes = Axes.Both,
                                                Padding = new MarginPadding { Top = 2 },
                                                Child = chips,
                                            },
                                        },
                                    },
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 3,
                                        Masking = true,
                                        CornerRadius = 1.5f,
                                        Children = new Drawable[]
                                        {
                                            new Box
                                            {
                                                RelativeSizeAxes = Axes.Both,
                                                Colour = BenchmarkColours.BORDER,
                                            },
                                            progress = new Box
                                            {
                                                RelativeSizeAxes = Axes.Both,
                                                Width = 0,
                                                Colour = BenchmarkColours.ACCENT,
                                            },
                                        },
                                    },
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 16,
                                        Child = status = new SpriteText
                                        {
                                            Font = BenchmarkColours.Font(13),
                                            Colour = BenchmarkColours.TEXT_DIM,
                                        },
                                    },
                                },
                            },
                        },
                        new Drawable[]
                        {
                            new BasicScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = table = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new Vector2(0, spacing),
                                    Padding = new MarginPadding { Horizontal = 24, Bottom = 16 },
                                },
                            },
                        },
                        new Drawable[]
                        {
                            detail = new CellDetail { RelativeSizeAxes = Axes.Both },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            design.BindValueChanged(d =>
            {
                designButton.Text = d.NewValue == BenchmarkDesign.Factorial ? "Factorial design" : "One factor at a time";
                showResult();
            }, true);

            rounds.BindValueChanged(r =>
            {
                roundsText.Text = $"{r.NewValue} {(r.NewValue == 1 ? "round" : "rounds")}";
                showResult();
            }, true);

            setRunning(false);

            string path = Path.Combine(options.ResultsDirectory, BenchmarkMatrixResult.FILENAME);

            Task.Run(() => BenchmarkMatrixResult.Load(path)).ContinueWith(t => Schedule(() =>
            {
                if (t.IsFaulted)
                    Logger.Error(t.Exception, $"Benchmark results in {path} could not be read");

                result = t.IsCompletedSuccessfully ? t.GetResultSafely() : null;
                resultLoaded = true;
                status.Text = result == null ? $"No results yet in {options.ResultsDirectory}. Choose what to vary, then run." : $"Results in {options.ResultsDirectory}";
                showResult();
            }));
        }

        private void toggleRun()
        {
            if (runner != null)
            {
                cancellation?.Cancel();
                return;
            }

            var runOptions = new BenchmarkRunOptions
            {
                ResultsDirectory = options.ResultsDirectory,
                Design = design.Value,
                Factors = includedFactors().Select(f => f.Name).ToArray(),
                Cases = options.Cases,
                Rounds = rounds.Value,
                WarmupFrames = options.WarmupFrames,
                MeasuredFrames = options.MeasuredFrames,
            };

            BenchmarkMatrixRunner newRunner;

            try
            {
                newRunner = new BenchmarkMatrixRunner(matrix, runOptions, createProcess);
            }
            catch (Exception e)
            {
                status.Text = e.Message;
                return;
            }

            runner = newRunner;
            cancellation = new CancellationTokenSource();

            newRunner.Changed += () => Schedule(() =>
            {
                if (runner != newRunner)
                    return;

                result = newRunner.Result;
                status.Text = newRunner.Status;
                progress.ResizeWidthTo(newRunner.TotalRuns == 0 ? 0 : (float)newRunner.CompletedRuns / newRunner.TotalRuns, 300, Easing.OutQuint);
                showResult();
            });

            setRunning(true);

            var token = cancellation.Token;

            Task.Run(() => newRunner.RunAsync(token), token).ContinueWith(t => Schedule(() =>
            {
                runner = null;
                setRunning(false);

                if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException)
                    status.Text = $"Stopped after {newRunner.CompletedRuns} of {newRunner.TotalRuns} runs";
                else if (t.IsFaulted)
                {
                    Logger.Error(t.Exception, "The benchmark matrix failed");
                    status.Text = $"Failed: {t.Exception?.InnerException?.Message}";
                }

                result = newRunner.Result;
                selectedCase = selectedConfiguration = null;
                showResult();
            }));
        }

        private void setRunning(bool running)
        {
            runButton.Text = running ? "Stop" : "Run";
            runButton.BackgroundColour = running ? BenchmarkColours.SLOWER.Darken(0.6f) : BenchmarkColours.ACCENT.Darken(0.55f);
            designButton.Enabled.Value = !running;

            foreach (var chip in factorChips.Values)
                chip.Locked.Value = running;

            // The window should take as little of the machine as possible from the processes being measured.
            if (running && throttledFrom == null)
            {
                throttledFrom = (host.MaximumUpdateHz, host.MaximumDrawHz);
                host.MaximumUpdateHz = host.MaximumDrawHz = 15;
            }
            else if (!running && throttledFrom is { } previous)
            {
                host.MaximumUpdateHz = previous.Update;
                host.MaximumDrawHz = previous.Draw;
                throttledFrom = null;
            }
        }

        private IEnumerable<BenchmarkFactor> includedFactors() => matrix.Factors.Where(f => factorChips[f].Included.Value);

        /// <summary>
        /// Rebuilds the table from the current results, or from the planned configurations when there are none.
        /// </summary>
        private void showResult()
        {
            if (!IsLoaded || !resultLoaded)
                return;

            var shown = result ?? plannedResult();
            bool running = runner != null;

            info.Text = shown.Runs.Count == 0
                ? $"{shown.Configurations.Count} configurations × {shown.Cases.Count} cases × {rounds.Value} rounds, {shown.WarmupFrames} warm-up and {shown.MeasuredFrames} measured frames each, {shown.Width}×{shown.Height}"
                : $"{shown.Device}  ·  {shown.Width}×{shown.Height}  ·  {shown.MeasuredFrames} frames after {shown.WarmupFrames} warm-up  ·  {shown.Rounds} rounds  ·  "
                  + $"{(shown.Design == BenchmarkDesign.Factorial ? "factorial" : "one factor at a time")}  ·  {shown.Started:yyyy-MM-dd HH:mm}"
                  + (shown.Completed == null && !running ? "  ·  incomplete" : string.Empty);

            table.Clear();
            cells.Clear();

            int columns = shown.Configurations.Count;
            var columnDimensions = new[] { new Dimension(GridSizeMode.Absolute, case_column_width) }
                                   .Concat(Enumerable.Range(0, columns).SelectMany(_ => new[] { new Dimension(GridSizeMode.Absolute, spacing), new Dimension() }))
                                   .ToArray();

            table.Add(new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                Height = header_row_height,
                ColumnDimensions = columnDimensions,
                Content = new[]
                {
                    new Drawable[] { columnHeader("Case", "median frame time; change from baseline", false) }
                        .Concat(shown.Configurations.SelectMany(c => new[]
                        {
                            Empty(),
                            columnHeader(c.Name, c.Changes.Count == 0 ? "baseline" : c.Changes.Count > 2 ? string.Join(", ", c.Changes) : describe(c), c.Changes.Count == 0),
                        })).ToArray(),
                },
            });

            foreach (var benchmarkCase in shown.Cases)
            {
                var baseline = shown.GetCell(benchmarkCase.Name, shown.Baseline);
                var rowCells = shown.Configurations.Select(c => shown.GetCell(benchmarkCase.Name, c.Name)).ToArray();
                double rowMaximum = rowCells.Where(c => c.HasData).Select(c => c.Update + c.Draw + c.Gpu).DefaultIfEmpty(0).Max();

                var row = new List<Drawable> { caseHeader(benchmarkCase) };

                foreach (var cell in rowCells)
                {
                    bool pending = running && shown.Runs.Count(r => r.Configuration == cell.Configuration && r.Round != BenchmarkRun.PRIMING_ROUND) < shown.Rounds;
                    var view = new MatrixCell(cell, baseline, shown.GetPixelDifference(cell.Case, cell.Configuration), rowMaximum, pending)
                    {
                        RelativeSizeAxes = Axes.X,
                    };

                    string caseName = cell.Case, configuration = cell.Configuration;
                    view.Action = () => select(caseName, configuration);

                    cells[(caseName, configuration)] = view;
                    row.Add(Empty());
                    row.Add(view);
                }

                table.Add(new GridContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = MatrixCell.HEIGHT,
                    ColumnDimensions = columnDimensions,
                    Content = new[] { row.ToArray() },
                });
            }

            if (selectedCase == null || selectedConfiguration == null || !cells.ContainsKey((selectedCase, selectedConfiguration)))
                (selectedCase, selectedConfiguration) = defaultSelection(shown);

            select(selectedCase, selectedConfiguration);
        }

        private void select(string? caseName, string? configuration)
        {
            selectedCase = caseName;
            selectedConfiguration = configuration;

            foreach (var ((c, config), view) in cells)
                view.Selected.Value = c == caseName && config == configuration;

            if (caseName != null && configuration != null)
                detail.Show(result ?? plannedResult(), options.ResultsDirectory, caseName, configuration);
        }

        /// <summary>
        /// The cell that changed most significantly, or the first that is not the baseline.
        /// </summary>
        private static (string?, string?) defaultSelection(BenchmarkMatrixResult shown)
        {
            var candidates = shown.Cases.SelectMany(c => shown.Configurations.Skip(1).Select(config => shown.GetCell(c.Name, config.Name))).ToArray();

            var best = candidates.Select(cell => (cell, baseline: shown.GetCell(cell.Case, shown.Baseline)))
                                 .Where(p => p.cell.IsSignificantlyDifferentFrom(p.baseline))
                                 .OrderByDescending(p => Math.Abs(p.cell.ChangeFrom(p.baseline) ?? 0))
                                 .Select(p => p.cell)
                                 .FirstOrDefault()
                       ?? candidates.FirstOrDefault(c => c.HasData)
                       ?? candidates.FirstOrDefault();

            if (best != null)
                return (best.Case, best.Configuration);

            return (shown.Cases.FirstOrDefault()?.Name, shown.Configurations.FirstOrDefault()?.Name);
        }

        private BenchmarkMatrixResult plannedResult()
        {
            var configurations = matrix.CreateConfigurations(includedFactors().Select(f => f.Name), design.Value);
            var cases = options.Cases == null ? matrix.Cases : matrix.Cases.Where(c => options.Cases.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToArray();

            return new BenchmarkMatrixResult
            {
                Matrix = matrix.Name,
                Width = matrix.Width,
                Height = matrix.Height,
                WarmupFrames = options.WarmupFrames ?? matrix.WarmupFrames,
                MeasuredFrames = options.MeasuredFrames ?? matrix.MeasuredFrames,
                Rounds = rounds.Value,
                Design = design.Value,
                Configurations = configurations.Select(c => new BenchmarkMatrixResult.ConfigurationInfo { Name = c.Name, Changes = c.Changes.ToList() }).ToList(),
                Cases = cases.Select(c => new BenchmarkMatrixResult.CaseInfo { Name = c.Name, Scene = c.SceneType.Name, Description = c.Description }).ToList(),
            };
        }

        private string describe(BenchmarkMatrixResult.ConfigurationInfo configuration)
        {
            // Name the switches behind a single change, for example the environment variable.
            var factor = configuration.Changes.Count == 1 ? matrix.Factors.FirstOrDefault(f => configuration.Changes[0].EndsWith(f.Name, StringComparison.Ordinal)) : null;
            return factor?.Description ?? string.Empty;
        }

        private static Drawable columnHeader(string title, string subtitle, bool isBaseline) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.Both,
            Direction = FillDirection.Vertical,
            Padding = new MarginPadding { Top = 14, Horizontal = 2 },
            Spacing = new Vector2(0, 3),
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Text = title,
                    Font = BenchmarkColours.Font(15, bold: true),
                    Colour = isBaseline ? BenchmarkColours.ACCENT : BenchmarkColours.TEXT,
                    Truncate = true,
                    RelativeSizeAxes = Axes.X,
                },
                new SpriteText
                {
                    Text = subtitle,
                    Font = BenchmarkColours.Font(12, condensed: true),
                    Colour = BenchmarkColours.TEXT_FAINT,
                    Truncate = true,
                    RelativeSizeAxes = Axes.X,
                },
            },
        };

        private static Drawable caseHeader(BenchmarkMatrixResult.CaseInfo benchmarkCase) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.Both,
            Direction = FillDirection.Vertical,
            Padding = new MarginPadding { Top = 10, Right = 12 },
            Spacing = new Vector2(0, 4),
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Text = benchmarkCase.Name,
                    Font = BenchmarkColours.Font(17, bold: true),
                    Colour = BenchmarkColours.TEXT,
                    Truncate = true,
                    RelativeSizeAxes = Axes.X,
                },
                new SpriteText
                {
                    Text = benchmarkCase.Scene,
                    Font = BenchmarkColours.Font(12, condensed: true),
                    Colour = BenchmarkColours.ACCENT.Opacity(0.8f),
                    Truncate = true,
                    RelativeSizeAxes = Axes.X,
                },
                new TextFlowContainer(t =>
                {
                    t.Font = BenchmarkColours.Font(12, condensed: true);
                    t.Colour = BenchmarkColours.TEXT_FAINT;
                })
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Text = benchmarkCase.Description,
                },
            },
        };

        protected override void Dispose(bool isDisposing)
        {
            cancellation?.Cancel();
            base.Dispose(isDisposing);
        }
    }
}
