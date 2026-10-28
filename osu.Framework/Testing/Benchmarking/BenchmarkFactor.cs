// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// A switch whose effect a matrix measures, such as an optimisation that can be turned off.
    /// Each configuration runs in its own process, so a factor can set anything read at startup:
    /// environment variables and framework settings.
    /// </summary>
    public sealed class BenchmarkFactor
    {
        /// <summary>
        /// The name shown in results, unique within a matrix.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// What turning the factor on changes.
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// Whether the factor is on in the shipped configuration, which is the baseline.
        /// </summary>
        public bool EnabledByDefault { get; init; }

        /// <summary>
        /// Environment variables set while the factor is on.
        /// </summary>
        public IReadOnlyDictionary<string, string> EnabledEnvironment { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Environment variables set while the factor is off.
        /// </summary>
        public IReadOnlyDictionary<string, string> DisabledEnvironment { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Framework settings (by <see cref="Configuration.FrameworkSetting"/> name) used while the factor is on.
        /// </summary>
        public IReadOnlyDictionary<string, string> EnabledSettings { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Framework settings (by <see cref="Configuration.FrameworkSetting"/> name) used while the factor is off.
        /// </summary>
        public IReadOnlyDictionary<string, string> DisabledSettings { get; init; } = new Dictionary<string, string>();

        /// <summary>
        /// Whether a matrix includes the factor unless told otherwise.
        /// </summary>
        public bool IncludedByDefault { get; init; } = true;

        public BenchmarkFactor(string name)
        {
            Name = name;
        }
    }
}
