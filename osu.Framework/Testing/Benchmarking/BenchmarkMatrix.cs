// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// Benchmark cases measured under configurations built from factors.
    /// </summary>
    public sealed class BenchmarkMatrix
    {
        /// <summary>
        /// The name of the configuration with every factor in its default state.
        /// </summary>
        public const string BASELINE = "Defaults";

        public string Name { get; }

        public IReadOnlyList<BenchmarkCase> Cases { get; }

        public IReadOnlyList<BenchmarkFactor> Factors { get; }

        /// <summary>
        /// The size of the offscreen framebuffer.
        /// </summary>
        public int Width { get; init; } = 1920;

        /// <summary>
        /// The size of the offscreen framebuffer.
        /// </summary>
        public int Height { get; init; } = 1080;

        /// <summary>
        /// Frames from the start of each case to the start of measurement, including loading and setting up the scene.
        /// </summary>
        public int WarmupFrames { get; init; } = 120;

        /// <summary>
        /// Frames measured for each case.
        /// </summary>
        public int MeasuredFrames { get; init; } = 300;

        /// <summary>
        /// How many times each configuration is run. Rounds interleave configurations, so drift affects them alike.
        /// </summary>
        public int Rounds { get; init; } = 4;

        public BenchmarkMatrix(string name, IReadOnlyList<BenchmarkCase> cases, IReadOnlyList<BenchmarkFactor> factors)
        {
            if (cases.Select(c => c.Name).Distinct().Count() != cases.Count)
                throw new ArgumentException("Case names must be unique.", nameof(cases));
            if (factors.Select(f => f.Name).Distinct().Count() != factors.Count)
                throw new ArgumentException("Factor names must be unique.", nameof(factors));

            Name = name;
            Cases = cases;
            Factors = factors;
        }

        /// <summary>
        /// Environment variables that any factor sets. Runs clear them before applying their own.
        /// </summary>
        public IEnumerable<string> ControlledEnvironment => Factors.SelectMany(f => f.EnabledEnvironment.Keys.Concat(f.DisabledEnvironment.Keys)).Distinct();

        /// <summary>
        /// Builds the configurations that change the given factors from their defaults.
        /// The first is always the baseline.
        /// </summary>
        /// <param name="factorNames">The factors to vary. Others stay in their default state.</param>
        /// <param name="design">How the factors are combined.</param>
        public IReadOnlyList<BenchmarkConfiguration> CreateConfigurations(IEnumerable<string> factorNames, BenchmarkDesign design)
        {
            var varied = factorNames.Select(n => Factors.SingleOrDefault(f => f.Name == n) ?? throw new ArgumentException($"Unknown factor \"{n}\".", nameof(factorNames)))
                                    .Distinct()
                                    .ToArray();

            var flipSets = new List<BenchmarkFactor[]> { Array.Empty<BenchmarkFactor>() };

            switch (design)
            {
                case BenchmarkDesign.OneFactorAtATime:
                    flipSets.AddRange(varied.Select(f => new[] { f }));

                    // The combined effect, which is not always the sum of the separate ones.
                    if (varied.Length > 1)
                        flipSets.Add(varied);
                    break;

                case BenchmarkDesign.Factorial:
                    if (varied.Length > 6)
                        throw new ArgumentException("A factorial design of more than six factors has too many configurations to run.", nameof(factorNames));

                    for (int mask = 1; mask < 1 << varied.Length; mask++)
                        flipSets.Add(varied.Where((_, i) => (mask & (1 << i)) != 0).ToArray());

                    flipSets.Sort((a, b) => a.Length.CompareTo(b.Length));
                    break;
            }

            return flipSets.Select(create).ToArray();
        }

        private BenchmarkConfiguration create(BenchmarkFactor[] flipped)
        {
            var environment = new Dictionary<string, string>();
            var settings = new Dictionary<string, string>();
            var changes = new List<string>();

            foreach (var factor in Factors)
            {
                bool enabled = factor.EnabledByDefault != flipped.Contains(factor);

                foreach (var (key, value) in enabled ? factor.EnabledEnvironment : factor.DisabledEnvironment)
                    environment[key] = value;
                foreach (var (key, value) in enabled ? factor.EnabledSettings : factor.DisabledSettings)
                    settings[key] = value;

                if (flipped.Contains(factor))
                    changes.Add($"{(enabled ? '+' : '−')}{factor.Name}");
            }

            string name = changes.Count == 0 ? BASELINE
                : changes.Count <= 2 ? string.Join(", ", changes)
                : $"All {changes.Count} changed";

            return new BenchmarkConfiguration(name, changes, environment, settings);
        }
    }
}
