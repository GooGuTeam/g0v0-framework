// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using osu.Framework.Imaging;

namespace osu.Framework.Testing.Benchmarking
{
    /// <summary>
    /// How a configuration's capture of a case differs from the baseline's.
    /// </summary>
    public sealed class BenchmarkPixelDifference
    {
        /// <summary>
        /// A channel difference above this counts a pixel as differing; smaller ones are rounding and filtering.
        /// </summary>
        public const int THRESHOLD = 8;

        public string Case { get; set; } = string.Empty;

        public string Configuration { get; set; } = string.Empty;

        /// <summary>
        /// The largest difference of any channel of any pixel, out of 255.
        /// </summary>
        public int MaximumDifference { get; set; }

        /// <summary>
        /// The fraction of pixels with a channel differing by more than <see cref="THRESHOLD"/>.
        /// </summary>
        public double DifferingFraction { get; set; }

        /// <summary>
        /// Why the captures could not be compared, if they could not.
        /// </summary>
        public string? Error { get; set; }

        internal static BenchmarkPixelDifference Compare(string caseName, string configuration, string baselinePath, string path)
        {
            var result = new BenchmarkPixelDifference { Case = caseName, Configuration = configuration };

            using var baseline = load(baselinePath);
            using var image = load(path);

            if (baseline.Width != image.Width || baseline.Height != image.Height)
            {
                result.Error = "The captures differ in size.";
                return result;
            }

            long differing = 0;
            int maximum = 0;

            for (int y = 0; y < image.Height; y++)
            {
                var a = baseline.DangerousGetPixelRowMemory(y).Span;
                var b = image.DangerousGetPixelRowMemory(y).Span;

                for (int x = 0; x < a.Length; x++)
                {
                    int difference = Math.Max(Math.Max(Math.Abs(a[x].R - b[x].R), Math.Abs(a[x].G - b[x].G)), Math.Max(Math.Abs(a[x].B - b[x].B), Math.Abs(a[x].A - b[x].A)));

                    maximum = Math.Max(maximum, difference);
                    if (difference > THRESHOLD)
                        differing++;
                }
            }

            result.MaximumDifference = maximum;
            result.DifferingFraction = (double)differing / ((long)image.Width * image.Height);
            return result;

            static Image<Rgba32> load(string file)
            {
                using (var stream = File.OpenRead(file))
                    return Image.Load<Rgba32>(stream);
            }
        }
    }
}
