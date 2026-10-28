// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// The factor states of one benchmark process.
    /// </summary>
    public sealed class BenchmarkConfiguration
    {
        public string Name { get; }

        /// <summary>
        /// The factors changed from their defaults, as "+name" or "−name".
        /// </summary>
        public IReadOnlyList<string> Changes { get; }

        /// <summary>
        /// Environment variables for the process.
        /// </summary>
        public IReadOnlyDictionary<string, string> Environment { get; }

        /// <summary>
        /// Framework settings for the process, by <see cref="Configuration.FrameworkSetting"/> name.
        /// </summary>
        public IReadOnlyDictionary<string, string> Settings { get; }

        public bool IsBaseline => Changes.Count == 0;

        public BenchmarkConfiguration(string name, IReadOnlyList<string> changes, IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> settings)
        {
            Name = name;
            Changes = changes;
            Environment = environment;
            Settings = settings;
        }
    }
}
