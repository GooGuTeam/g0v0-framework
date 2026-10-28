// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Testing.Benchmarking.Display;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// Command-line entry to benchmark matrices.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>--benchmark-matrix</c> opens the results window, which can start runs.</item>
    /// <item><c>--benchmark-matrix --run</c> runs the matrix without a window and prints the results.</item>
    /// <item><c>--benchmark-matrix --capture-ui=file.png</c> renders the results window offscreen to an image.</item>
    /// </list>
    /// Options: <c>--results=dir</c>, <c>--design=factorial</c>, <c>--factor=name</c> and <c>--case=name</c> (repeatable),
    /// <c>--rounds=n</c>, <c>--warmup=n</c> and <c>--frames=n</c>.
    /// Each run starts this program again with <c>--benchmark-child</c>.
    /// </remarks>
    public static partial class BenchmarkProgram
    {
        public const string MATRIX_ARGUMENT = @"--benchmark-matrix";

        internal const string CHILD_ARGUMENT = @"--benchmark-child";

        private const string child_game_name = @"benchmark";

        /// <summary>
        /// Runs the benchmark command in <paramref name="args"/>, if there is one.
        /// </summary>
        /// <param name="args">The program's arguments.</param>
        /// <param name="matrix">The matrix to run or show.</param>
        /// <param name="createGame">Creates the game that hosts benchmark scenes and the results window, providing the resources the scenes need.</param>
        /// <returns>Whether <paramref name="args"/> contained a benchmark command.</returns>
        public static bool TryRun(string[] args, BenchmarkMatrix matrix, Func<Game> createGame)
        {
            if (args.Contains(CHILD_ARGUMENT))
            {
                runChild(args, matrix, createGame);
                return true;
            }

            if (!args.Contains(MATRIX_ARGUMENT))
                return false;

            var options = new BenchmarkRunOptions
            {
                ResultsDirectory = value(args, @"--results") ?? GetDefaultResultsDirectory(matrix),
                Design = value(args, @"--design")?.Equals(@"factorial", StringComparison.OrdinalIgnoreCase) == true ? BenchmarkDesign.Factorial : BenchmarkDesign.OneFactorAtATime,
                Factors = values(args, @"--factor") is { Length: > 0 } factors ? factors : null,
                Cases = values(args, @"--case") is { Length: > 0 } cases ? cases : null,
                Rounds = integer(args, @"--rounds"),
                WarmupFrames = integer(args, @"--warmup"),
                MeasuredFrames = integer(args, @"--frames"),
            };

            if (args.Contains(@"--run"))
                runInConsole(matrix, options);
            else if (value(args, @"--capture-ui") is string capturePath)
                captureDisplay(matrix, options, createGame, Path.GetFullPath(capturePath));
            else
            {
                using (var host = Host.GetSuitableDesktopHost(@"benchmark-matrix"))
                {
                    var game = createGame();
                    game.Add(new AsyncLoader(new BenchmarkMatrixDisplay(matrix, options, CreateProcess)));
                    host.Run(game);
                }
            }

            return true;
        }

        /// <summary>
        /// Where results go unless <c>--results</c> says otherwise.
        /// </summary>
        public static string GetDefaultResultsDirectory(BenchmarkMatrix matrix)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"g0v0-benchmarks", BenchmarkMatrixRunner.GetFileName(matrix.Name));

        /// <summary>
        /// Starts this program again with other arguments.
        /// </summary>
        public static ProcessStartInfo CreateProcess(IEnumerable<string> arguments)
        {
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("The program's path is unknown.");
            var info = new ProcessStartInfo(path);

            // Programs started through the dotnet host name their assembly first.
            if (Path.GetFileNameWithoutExtension(path).Equals(@"dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(RuntimeInfo.EntryAssembly.Location);

            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);

            return info;
        }

        private static void runInConsole(BenchmarkMatrix matrix, BenchmarkRunOptions options)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var runner = new BenchmarkMatrixRunner(matrix, options, CreateProcess);
            string lastStatus = string.Empty;

            runner.Changed += () =>
            {
                if (runner.Status == lastStatus)
                    return;

                lastStatus = runner.Status;
                Console.WriteLine(lastStatus);
            };

            Console.WriteLine($"{runner.Configurations.Count} configurations × {runner.Cases.Count} cases × {runner.Rounds} rounds, results in {options.ResultsDirectory}");
            runner.RunAsync().GetAwaiter().GetResult();

            Console.WriteLine();
            Console.WriteLine(BenchmarkReport.CreateMarkdown(runner.Result));

            foreach (var run in runner.Result.Runs.Where(r => r.Error != null))
                Console.WriteLine($"Run {run.Round + 1} of {run.Configuration} failed: {run.Error}");
        }

        private static void captureDisplay(BenchmarkMatrix matrix, BenchmarkRunOptions options, Func<Game> createGame, string path)
        {
            using (var host = new OffscreenGameHost(@"benchmark-display", 1920, 1080, Path.Combine(options.ResultsDirectory, BenchmarkMatrixRunner.HOST_DIRECTORY)))
            {
                var game = createGame();
                var display = new BenchmarkMatrixDisplay(matrix, options, CreateProcess);

                game.Add(new AsyncLoader(display));
                game.Add(new DisplayCapture(display, path));
                host.Run(game);
            }
        }

        private static void runChild(string[] args, BenchmarkMatrix matrix, Func<Game> createGame)
        {
            string results = value(args, @"--results") ?? throw new ArgumentException("No results directory.");
            string[] caseNames = values(args, @"--case");

            var run = new BenchmarkRun
            {
                Configuration = value(args, @"--configuration") ?? BenchmarkMatrix.BASELINE,
                Round = integer(args, @"--round") ?? 0,
            };

            BenchmarkProcessPriority.Raise();

            string storage = Path.Combine(results, BenchmarkMatrixRunner.HOST_DIRECTORY);

            // Each process starts from the framework's defaults and its configuration's settings, whatever an earlier process saved.
            string gameStorage = Path.Combine(storage, child_game_name);
            Directory.CreateDirectory(gameStorage);
            File.WriteAllLines(Path.Combine(gameStorage, FrameworkConfigManager.FILENAME), values(args, @"--setting").Select(s =>
            {
                int separator = s.IndexOf('=');
                return $@"{s[..separator]} = {s[(separator + 1)..]}";
            }));

            using (var host = new OffscreenGameHost(child_game_name, matrix.Width, matrix.Height, storage))
            {
                var game = createGame();

                game.Add(new BenchmarkCaseRunner(matrix.Cases.Where(c => caseNames.Contains(c.Name)).ToArray(),
                    integer(args, @"--warmup") ?? matrix.WarmupFrames,
                    integer(args, @"--frames") ?? matrix.MeasuredFrames,
                    run, results, args.Contains(@"--prime")));

                host.Run(game);
            }
        }

        private static string? value(string[] args, string name)
            => args.LastOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

        private static string[] values(string[] args, string name)
            => args.Where(a => a.StartsWith(name + "=", StringComparison.Ordinal)).Select(a => a[(name.Length + 1)..]).ToArray();

        private static int? integer(string[] args, string name)
            => int.TryParse(value(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : null;

        /// <summary>
        /// Loads a drawable in the background once the game has started. Loading the display with the game would nest too deeply for the main thread's stack.
        /// </summary>
        private partial class AsyncLoader : CompositeDrawable
        {
            private readonly Drawable content;

            public AsyncLoader(Drawable content)
            {
                this.content = content;
                RelativeSizeAxes = Axes.Both;
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();
                LoadComponentAsync(content, AddInternal);
            }
        }

        /// <summary>
        /// Saves the display once its contents have loaded, then exits.
        /// </summary>
        private partial class DisplayCapture : CompositeDrawable
        {
            private readonly BenchmarkMatrixDisplay display;
            private readonly string path;
            private readonly Stopwatch elapsed = new Stopwatch();

            private bool capturing;
            private int readyFrames;

            [Resolved]
            private GameHost host { get; set; } = null!;

            public DisplayCapture(BenchmarkMatrixDisplay display, string path)
            {
                this.display = display;
                this.path = path;
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();
                elapsed.Start();
            }

            protected override void Update()
            {
                base.Update();

                // Frames are fast and unthrottled, so wait for loading in real time as well as for transforms to settle,
                // then for a few more frames so that the last loaded image has been uploaded and drawn.
                readyFrames = display.IsReady || elapsed.Elapsed.TotalSeconds > 15 ? readyFrames + 1 : 0;

                if (capturing || Time.Current < 2000 || readyFrames < 10)
                    return;

                capturing = true;

                ((OffscreenGameHost)host).CaptureAsync().ContinueWith(t =>
                {
                    try
                    {
                        using (var image = t.GetAwaiter().GetResult())
                        using (var stream = File.Create(path))
                            image.SaveAsPng(stream);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, $"The display could not be saved to {path}");
                    }
                    finally
                    {
                        Schedule(host.Exit);
                    }
                });
            }
        }
    }
}
