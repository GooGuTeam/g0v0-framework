// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Imaging;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Testing.Drawables.Steps;
using osu.Framework.Utils;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// Runs benchmark cases one after another in an <see cref="OffscreenGameHost"/>, then exits the host.
    /// </summary>
    /// <remarks>
    /// Every case advances scene time by the same number of frames whatever the configuration (as long as it is set up within its warm-up),
    /// and frames that hold a scene still do not advance it. So scene time, and with it every animation, is the same when each frame is captured.
    /// </remarks>
    public partial class BenchmarkCaseRunner : CompositeDrawable
    {
        private const int frames_between_cases = 2;

        /// <summary>
        /// Real time from the start of a case before it is measured. After its warm-up frames the scene is held still until then,
        /// so that tiered compilation and CPU and GPU clocks settle without changing what is drawn when.
        /// </summary>
        private static readonly TimeSpan minimum_case_time = TimeSpan.FromSeconds(1);

        /// <summary>
        /// As <see cref="minimum_case_time"/>, for the first case, which also compiles everything the cases share.
        /// </summary>
        private static readonly TimeSpan minimum_first_case_time = TimeSpan.FromSeconds(3);

        private readonly IReadOnlyList<BenchmarkCase> cases;
        private readonly int warmupFrames;
        private readonly int measuredFrames;
        private readonly BenchmarkRun run;
        private readonly string resultsDirectory;
        private readonly bool priming;

        private readonly Container sceneContainer;

        [Resolved]
        private GameHost host { get; set; } = null!;

        private int caseIndex = -1;
        private int caseFrame;
        private readonly Stopwatch caseTime = new Stopwatch();
        private IEnumerator<bool>? caseRoutine;
        private BenchmarkCaseRun? currentResult;

        /// <param name="cases">The cases to run.</param>
        /// <param name="warmupFrames">Frames from the start of each case to the start of measurement, including loading and setting up the scene.</param>
        /// <param name="measuredFrames">Frames measured for each case.</param>
        /// <param name="run">Receives the results, and is written to the results directory after each case. Its configuration and round name the file.</param>
        /// <param name="resultsDirectory">The directory of the matrix results.</param>
        /// <param name="priming">Whether to save each case's frame at the end of its warm-up instead of measuring it (see <see cref="BenchmarkRun.PRIMING_ROUND"/>).</param>
        public BenchmarkCaseRunner(IReadOnlyList<BenchmarkCase> cases, int warmupFrames, int measuredFrames, BenchmarkRun run, string resultsDirectory, bool priming)
        {
            this.cases = cases;
            this.warmupFrames = warmupFrames;
            this.measuredFrames = measuredFrames;
            this.run = run;
            this.resultsDirectory = resultsDirectory;
            this.priming = priming;

            RelativeSizeAxes = Axes.Both;
            InternalChild = sceneContainer = new Container { RelativeSizeAxes = Axes.Both };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (host is not OffscreenGameHost)
                throw new InvalidOperationException($"Benchmarks run in an {nameof(OffscreenGameHost)}.");

            run.Device = ((OffscreenGameHost)host).DeviceDescription;
            run.Started = DateTimeOffset.Now;
            nextCase();
        }

        protected override void Update()
        {
            base.Update();

            if (caseRoutine == null)
                return;

            caseFrame++;

            try
            {
                if (caseRoutine.MoveNext())
                    return;
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Benchmark case \"{cases[caseIndex].Name}\" failed");
                currentResult = new BenchmarkCaseRun { Case = cases[caseIndex].Name, Error = e.ToString() };
            }

            finishCase();
        }

        private void nextCase()
        {
            caseRoutine = null;

            if (++caseIndex >= cases.Count)
            {
                save();
                host.Exit();
                return;
            }

            caseFrame = 0;
            caseTime.Restart();
            currentResult = null;
            caseRoutine = runCase(cases[caseIndex]).GetEnumerator();
        }

        private void finishCase()
        {
            caseRoutine = null;
            ((OffscreenGameHost)host).FreezeTime = false;

            var result = currentResult ?? new BenchmarkCaseRun { Case = cases[caseIndex].Name, Error = "The case finished without results." };
            result.Seconds = caseTime.Elapsed.TotalSeconds;
            run.Cases.Add(result);
            save();

            sceneContainer.Clear();

            // Garbage from this case should not be collected while the next is measured.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Scheduler.AddDelayed(nextCase, frames_between_cases * OffscreenGameHost.FRAME_STEP);
        }

        private IEnumerable<bool> runCase(BenchmarkCase benchmarkCase)
        {
            var offscreenHost = (OffscreenGameHost)host;

            // Scenes that use random values build the same content in every configuration.
            RNG.Seed(0);

            var scene = (TestScene)Activator.CreateInstance(benchmarkCase.SceneType)!;
            sceneContainer.Child = scene;

            // Steps are added while the scene loads, some of them scheduled.
            while (!scene.IsLoaded || caseFrame < 3)
                yield return true;

            if (benchmarkCase.Test != null)
            {
                var method = benchmarkCase.SceneType.GetMethod(benchmarkCase.Test, BindingFlags.Public | BindingFlags.Instance)
                             ?? throw new InvalidOperationException($"{benchmarkCase.SceneType.Name} has no test {benchmarkCase.Test}.");

                var setUpMethods = ReflectionUtils.GetMethodsWithAttribute(benchmarkCase.SceneType, typeof(SetUpAttribute), true);

                if (setUpMethods.Length > 0)
                    scene.AddStep(@"[SetUp]", () => setUpMethods.ForEach(m => m.Invoke(scene, null)));

                scene.RunSetUpSteps();
                method.Invoke(scene, benchmarkCase.TestArguments);
            }

            if (benchmarkCase.Test != null || benchmarkCase.RunAllSteps)
            {
                bool finished = false;
                Exception? error = null;

                scene.RunAllSteps(() => finished = true, (_, e) =>
                {
                    error = e;
                    finished = true;
                });

                while (!finished)
                    yield return true;

                if (error != null)
                    throw new InvalidOperationException($"A step of {scene.GetType().Name} failed.", error);
            }

            foreach (string step in benchmarkCase.Steps)
                findStep(scene, step).PerformStep();

            foreach (var (description, value) in benchmarkCase.Sliders)
                setSlider(scene, description, value);

            int setupFrames = caseFrame;

            if (setupFrames > warmupFrames)
                Logger.Log($"Benchmark case \"{benchmarkCase.Name}\" took {setupFrames} frames to set up, more than its warm-up, so its timing may differ between configurations.", level: LogLevel.Important);

            while (caseFrame < warmupFrames)
                yield return true;

            if (priming)
            {
                currentResult = new BenchmarkCaseRun { Case = benchmarkCase.Name, SetupFrames = setupFrames };

                Task<Image<Rgba32>> frame = offscreenHost.CaptureAsync();

                while (!frame.IsCompleted)
                    yield return true;

                using (var image = frame.GetAwaiter().GetResult())
                {
                    string path = BenchmarkMatrixRunner.GetCapturePath(resultsDirectory, run.Configuration, benchmarkCase.Name);

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    using (var stream = File.Create(path))
                        image.SaveAsPng(stream);

                    currentResult.Capture = Path.GetRelativePath(resultsDirectory, path).Replace('\\', '/');
                }

                yield break;
            }

            var minimumTime = caseIndex == 0 ? minimum_first_case_time : minimum_case_time;

            offscreenHost.FreezeTime = true;

            while (caseTime.Elapsed < minimumTime)
                yield return true;

            offscreenHost.FreezeTime = false;
            offscreenHost.StartRecording(measuredFrames);

            OffscreenFrameRecording? recording;

            while ((recording = offscreenHost.TakeRecording()) == null)
                yield return true;

            currentResult = BenchmarkCaseRun.FromRecording(benchmarkCase.Name, setupFrames, recording);
        }

        private void save() => BenchmarkMatrixResult.SaveRun(run, BenchmarkMatrixRunner.GetRunPath(resultsDirectory, run.Configuration, run.Round));

        private static StepButton findStep(TestScene scene, string text)
            => scene.StepsContainer.OfType<StepButton>().FirstOrDefault(s => s.Text.ToString().Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"{scene.GetType().Name} has no step \"{text}\".");

        private static void setSlider(TestScene scene, string description, double value)
        {
            // Slider steps show "description: value" and are generic over their value type.
            var slider = scene.StepsContainer.FirstOrDefault(d => d.GetType().IsGenericType
                                                                  && d.GetType().GetGenericTypeDefinition() == typeof(StepSlider<>)
                                                                  && d.ToString()?.StartsWith(description + ":", StringComparison.OrdinalIgnoreCase) == true)
                         ?? throw new InvalidOperationException($"{scene.GetType().Name} has no slider step \"{description}\".");

            var current = (IParseable)slider.GetType().GetProperties().First(p => p.Name == nameof(StepSlider<int>.Current)).GetValue(slider)!;
            current.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
    }
}
