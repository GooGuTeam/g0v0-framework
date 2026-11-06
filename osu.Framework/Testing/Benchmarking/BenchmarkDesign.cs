// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Testing.Benchmarking
{
    public enum BenchmarkDesign
    {
        /// <summary>
        /// The baseline, each factor changed alone, and all of them changed together.
        /// </summary>
        OneFactorAtATime,

        /// <summary>
        /// Every combination of the factors, which also shows how they interact.
        /// </summary>
        Factorial,
    }
}
