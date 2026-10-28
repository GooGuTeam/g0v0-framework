// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// Runs a matrix: one process per configuration and round, one after another, each running every case offscreen.
    /// </summary>
    public sealed class BenchmarkMatrixRunner
    {
        private const string runs_directory = @"runs";
        private const string captures_directory = @"captures";
        private const string logs_directory = @"logs";
        private const string history_directory = @"history";

        /// <summary>
        /// The directory holding each process's game storage, including its shader cache.
        /// </summary>
        internal const string HOST_DIRECTORY = @"host";

        /// <summary>
        /// The Markdown table written when a matrix finishes.
        /// </summary>
        public const string REPORT_FILENAME = @"report.md";

        private readonly BenchmarkMatrix matrix;
        private readonly BenchmarkRunOptions options;
        private readonly Func<IEnumerable<string>, ProcessStartInfo> createProcess;

        public IReadOnlyList<BenchmarkConfiguration> Configurations { get; }

        public IReadOnlyList<BenchmarkCase> Cases { get; }

        public int Rounds { get; }

        public int WarmupFrames { get; }

        public int MeasuredFrames { get; }

        /// <summary>
        /// The results so far. Replaced by a new instance after each run; read it from any thread.
        /// </summary>
        public BenchmarkMatrixResult Result { get; private set; }

        /// <summary>
        /// Processes to run: one per configuration and round, and the priming run of each configuration.
        /// </summary>
        public int TotalRuns => Configurations.Count * (Rounds + 1);

        public int CompletedRuns { get; private set; }

        /// <summary>
        /// What the runner is doing.
        /// </summary>
        public string Status { get; private set; } = string.Empty;

        /// <summary>
        /// Raised on a background thread whenever <see cref="Status"/> or <see cref="Result"/> change.
        /// </summary>
        public event Action? Changed;

        /// <param name="matrix">The matrix.</param>
        /// <param name="options">What to run.</param>
        /// <param name="createProcess">Starts this program with the given arguments, which it passes to <see cref="BenchmarkProgram.TryRun"/>.</param>
        public BenchmarkMatrixRunner(BenchmarkMatrix matrix, BenchmarkRunOptions options, Func<IEnumerable<string>, ProcessStartInfo> createProcess)
        {
            this.matrix = matrix;
            this.options = options;
            this.createProcess = createProcess;

            Configurations = matrix.CreateConfigurations(options.Factors ?? matrix.Factors.Where(f => f.IncludedByDefault).Select(f => f.Name), options.Design);
            Cases = options.Cases == null
                ? matrix.Cases
                : matrix.Cases.Where(c => options.Cases.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToArray();

            if (Cases.Count == 0)
                throw new ArgumentException("No cases match.", nameof(options));

            Rounds = Math.Max(1, options.Rounds ?? matrix.Rounds);
            WarmupFrames = Math.Max(1, options.WarmupFrames ?? matrix.WarmupFrames);
            MeasuredFrames = Math.Max(1, options.MeasuredFrames ?? matrix.MeasuredFrames);

            Result = createResult();
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            string root = options.ResultsDirectory;

            foreach (string directory in new[] { runs_directory, captures_directory, logs_directory })
            {
                string path = Path.Combine(root, directory);
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                Directory.CreateDirectory(path);
            }

            var result = createResult();
            result.Started = DateTimeOffset.Now;
            publish(result, "Starting");

            var stopwatch = Stopwatch.StartNew();

            // The first time a configuration runs, the shader caches miss and drivers keep compiling while it draws.
            // The priming round takes that cost, and captures the cases, before any are measured.
            for (int round = BenchmarkRun.PRIMING_ROUND; round < Rounds; round++)
            {
                // Rounds alternate direction, so that a steady drift (such as a laptop warming up) cancels over each pair of rounds
                // when configurations are compared round by round (see BenchmarkComparison).
                for (int i = 0; i < Configurations.Count; i++)
                {
                    var configuration = Configurations[round % 2 == 1 ? Configurations.Count - 1 - i : i];
                    string roundName = round == BenchmarkRun.PRIMING_ROUND ? "Priming" : $"Round {round + 1} of {Rounds}";

                    string remaining = CompletedRuns == 0 ? string.Empty : $", about {TimeSpan.FromTicks(stopwatch.Elapsed.Ticks / CompletedRuns * (TotalRuns - CompletedRuns)):m\\:ss} left";
                    publish(result, $"{roundName}: {configuration.Name} ({CompletedRuns + 1} of {TotalRuns}{remaining})");

                    var run = await runProcess(configuration, round, cancellationToken).ConfigureAwait(false);

                    result = copy(result);
                    result.Runs.Add(run);
                    CompletedRuns++;
                    publish(result, Status);
                }
            }

            publish(result, "Comparing captures");

            result = copy(result);
            result.PixelDifferences = await Task.Run(() => comparePixels(result), cancellationToken).ConfigureAwait(false);
            result.Completed = DateTimeOffset.Now;

            Directory.CreateDirectory(Path.Combine(root, history_directory));
            result.Save(Path.Combine(root, history_directory, $@"{result.Started:yyyyMMdd-HHmmss}.json"));
            await File.WriteAllTextAsync(Path.Combine(root, REPORT_FILENAME), BenchmarkReport.CreateMarkdown(result), CancellationToken.None).ConfigureAwait(false);

            publish(result, $"Finished {TotalRuns} runs in {stopwatch.Elapsed:m\\:ss}");
        }

        private async Task<BenchmarkRun> runProcess(BenchmarkConfiguration configuration, int round, CancellationToken cancellationToken)
        {
            string root = options.ResultsDirectory;

            var arguments = new List<string>
            {
                BenchmarkProgram.CHILD_ARGUMENT,
                $@"--results={root}",
                $@"--configuration={configuration.Name}",
                $@"--round={round}",
                $@"--warmup={WarmupFrames}",
                $@"--frames={MeasuredFrames}",
            };

            if (round == BenchmarkRun.PRIMING_ROUND)
                arguments.Add(@"--prime");

            arguments.AddRange(Cases.Select(c => $@"--case={c.Name}"));
            arguments.AddRange(configuration.Settings.Select(s => $@"--setting={s.Key}={s.Value}"));

            var info = createProcess(arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            foreach (string variable in matrix.ControlledEnvironment)
                info.Environment.Remove(variable);
            foreach (var (key, value) in configuration.Environment)
                info.Environment[key] = value;

            var output = new StringBuilder();
            var timeout = TimeSpan.FromSeconds(120 + Cases.Count * (WarmupFrames + MeasuredFrames) * 0.1);
            string? failure = null;

            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (_, e) => append(e.Data);
                process.ErrorDataReceived += (_, e) => append(e.Data);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);

                try
                {
                    await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

                    if (process.ExitCode != 0)
                        failure = $"The process exited with code {process.ExitCode}.";
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    failure = $"The process did not finish within {timeout:m\\:ss}.";
                }
            }

            string log;
            lock (output)
                log = output.ToString();

            await File.WriteAllTextAsync(Path.Combine(root, logs_directory, $@"{roundName(round)}-{GetFileName(configuration.Name)}.log"), log, CancellationToken.None).ConfigureAwait(false);

            BenchmarkRun? run = null;

            try
            {
                run = BenchmarkMatrixResult.LoadRun(GetRunPath(root, configuration.Name, round));
            }
            catch (Exception e)
            {
                failure ??= $"The results could not be read: {e.Message}";
            }

            run ??= new BenchmarkRun { Configuration = configuration.Name, Round = round };

            if (failure != null || run.Cases.Count < Cases.Count)
                run.Error = $"{failure ?? "The process stopped before finishing every case."}\n{tail(log)}";

            return run;

            void append(string? line)
            {
                if (line == null)
                    return;

                lock (output)
                    output.AppendLine(line);
            }
        }

        private List<BenchmarkPixelDifference> comparePixels(BenchmarkMatrixResult result)
        {
            var differences = new List<BenchmarkPixelDifference>();
            string root = options.ResultsDirectory;

            foreach (var benchmarkCase in Cases)
            {
                string? baseline = captureOf(Configurations[0].Name);

                foreach (var configuration in Configurations.Skip(1))
                {
                    string? capture = captureOf(configuration.Name);

                    if (baseline == null || capture == null)
                        continue;

                    try
                    {
                        differences.Add(BenchmarkPixelDifference.Compare(benchmarkCase.Name, configuration.Name, baseline, capture));
                    }
                    catch (Exception e)
                    {
                        differences.Add(new BenchmarkPixelDifference { Case = benchmarkCase.Name, Configuration = configuration.Name, Error = e.Message });
                    }
                }

                string? captureOf(string configuration)
                {
                    string? relative = result.Runs.FirstOrDefault(r => r.Configuration == configuration && r.Round == BenchmarkRun.PRIMING_ROUND)?
                                             .Cases.FirstOrDefault(c => c.Case == benchmarkCase.Name)?.Capture;

                    return relative == null ? null : Path.Combine(root, relative);
                }
            }

            return differences;
        }

        private void publish(BenchmarkMatrixResult result, string status)
        {
            Result = result;
            Status = status;

            Directory.CreateDirectory(options.ResultsDirectory);
            result.Save(Path.Combine(options.ResultsDirectory, BenchmarkMatrixResult.FILENAME));

            Changed?.Invoke();
        }

        private BenchmarkMatrixResult createResult() => new BenchmarkMatrixResult
        {
            Matrix = matrix.Name,
            Width = matrix.Width,
            Height = matrix.Height,
            WarmupFrames = WarmupFrames,
            MeasuredFrames = MeasuredFrames,
            Rounds = Rounds,
            Design = options.Design,
            Configurations = Configurations.Select(c => new BenchmarkMatrixResult.ConfigurationInfo
            {
                Name = c.Name,
                Changes = c.Changes.ToList(),
                Environment = c.Environment.ToDictionary(),
                Settings = c.Settings.ToDictionary(),
            }).ToList(),
            Cases = Cases.Select(c => new BenchmarkMatrixResult.CaseInfo
            {
                Name = c.Name,
                Scene = c.SceneType.Name,
                Description = c.Description,
            }).ToList(),
        };

        /// <summary>
        /// A shallow copy, so that readers of <see cref="Result"/> never see a list being changed.
        /// </summary>
        private static BenchmarkMatrixResult copy(BenchmarkMatrixResult result) => new BenchmarkMatrixResult
        {
            Matrix = result.Matrix,
            Started = result.Started,
            Completed = result.Completed,
            Width = result.Width,
            Height = result.Height,
            WarmupFrames = result.WarmupFrames,
            MeasuredFrames = result.MeasuredFrames,
            Rounds = result.Rounds,
            Design = result.Design,
            Configurations = result.Configurations,
            Cases = result.Cases,
            Runs = result.Runs.ToList(),
            PixelDifferences = result.PixelDifferences.ToList(),
        };

        private static string tail(string log)
        {
            string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 20)));
        }

        /// <summary>
        /// The file a process writes its results to.
        /// </summary>
        public static string GetRunPath(string resultsDirectory, string configuration, int round)
            => Path.Combine(resultsDirectory, runs_directory, $@"{roundName(round)}-{GetFileName(configuration)}.json");

        /// <summary>
        /// The file a case's last frame is saved to.
        /// </summary>
        public static string GetCapturePath(string resultsDirectory, string configuration, string caseName)
            => Path.Combine(resultsDirectory, captures_directory, GetFileName(configuration), GetFileName(caseName) + @".png");

        private static string roundName(int round) => round == BenchmarkRun.PRIMING_ROUND ? @"prime" : $@"r{round}";

        /// <summary>
        /// Makes a name safe to use in a path.
        /// </summary>
        public static string GetFileName(string name)
        {
            var builder = new StringBuilder(name.Length);

            foreach (char c in name)
            {
                if (char.IsAsciiLetterOrDigit(c) || c == '+' || c == '-' || c == '_')
                    builder.Append(c);
                else if (c == '−')
                    builder.Append('-');
                else if (builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');
            }

            return builder.ToString().Trim('_');
        }
    }
}
