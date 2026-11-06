// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// Everything measured by one run of a matrix, saved as <c>matrix.json</c> in its results directory.
    /// </summary>
    public sealed class BenchmarkMatrixResult
    {
        public const string FILENAME = @"matrix.json";

        private static readonly JsonSerializerOptions json_options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
        };

        public string Matrix { get; set; } = string.Empty;

        public DateTimeOffset Started { get; set; }

        /// <summary>
        /// When the last run finished, or <c>null</c> while runs remain.
        /// </summary>
        public DateTimeOffset? Completed { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int WarmupFrames { get; set; }

        public int MeasuredFrames { get; set; }

        public int Rounds { get; set; }

        public BenchmarkDesign Design { get; set; }

        /// <summary>
        /// The configurations in display order. The first is the baseline.
        /// </summary>
        public List<ConfigurationInfo> Configurations { get; set; } = new List<ConfigurationInfo>();

        public List<CaseInfo> Cases { get; set; } = new List<CaseInfo>();

        public List<BenchmarkRun> Runs { get; set; } = new List<BenchmarkRun>();

        /// <summary>
        /// How each configuration's capture differs from the baseline's, for each case.
        /// </summary>
        public List<BenchmarkPixelDifference> PixelDifferences { get; set; } = new List<BenchmarkPixelDifference>();

        [JsonIgnore]
        public string Baseline => Configurations.FirstOrDefault()?.Name ?? BenchmarkMatrix.BASELINE;

        /// <summary>
        /// The graphics device of the first run.
        /// </summary>
        [JsonIgnore]
        public string Device => Runs.FirstOrDefault(r => !string.IsNullOrEmpty(r.Device))?.Device ?? string.Empty;

        /// <summary>
        /// Summarises the runs of a case under a configuration.
        /// </summary>
        public BenchmarkCell GetCell(string caseName, string configuration)
            => new BenchmarkCell(caseName, configuration, Runs.Where(r => r.Configuration == configuration && r.Round != BenchmarkRun.PRIMING_ROUND)
                                                              .OrderBy(r => r.Round)
                                                              .Select(r => (r, r.Cases.FirstOrDefault(c => c.Case == caseName)))
                                                              .Where(p => p.Item2 != null || p.r.Error != null)
                                                              .Select(p => (p.r.Round, p.Item2 ?? new BenchmarkCaseRun { Case = caseName, Error = p.r.Error }))
                                                              .ToArray());

        public BenchmarkPixelDifference? GetPixelDifference(string caseName, string configuration)
            => PixelDifferences.FirstOrDefault(d => d.Case == caseName && d.Configuration == configuration);

        public void Save(string path)
        {
            string temporary = path + @".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, json_options));
            File.Move(temporary, path, true);
        }

        public static BenchmarkMatrixResult? Load(string path)
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<BenchmarkMatrixResult>(File.ReadAllText(path), json_options);
        }

        internal static void SaveRun(BenchmarkRun run, string path)
        {
            string temporary = path + @".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(run, json_options));
            File.Move(temporary, path, true);
        }

        internal static BenchmarkRun? LoadRun(string path)
            => File.Exists(path) ? JsonSerializer.Deserialize<BenchmarkRun>(File.ReadAllText(path), json_options) : null;

        public sealed class ConfigurationInfo
        {
            public string Name { get; set; } = string.Empty;

            public List<string> Changes { get; set; } = new List<string>();

            public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();

            public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();
        }

        public sealed class CaseInfo
        {
            public string Name { get; set; } = string.Empty;

            public string Scene { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;
        }
    }
}
