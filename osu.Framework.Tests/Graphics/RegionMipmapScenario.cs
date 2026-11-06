// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Shared data for comparing regional mipmap generation with whole-texture generation on a GPU backend.
    /// </summary>
    internal static class RegionMipmapScenario
    {
        public const int SIZE = 256;

        /// <summary>
        /// Unaligned patches, including a texture corner, a single texel and adjacent pieces like an atlas glyph and its padding.
        /// </summary>
        public static readonly RectangleI[] PATCHES =
        {
            new RectangleI(13, 17, 20, 9),
            new RectangleI(100, 40, 33, 50),
            new RectangleI(88, 40, 12, 50),
            new RectangleI(5, 200, 7, 7),
            new RectangleI(240, 240, 16, 16),
            new RectangleI(64, 64, 1, 1),
        };

        public static Image<Rgba32> CreateBase() => random(SIZE, SIZE, 1);

        public static Image<Rgba32> CreatePatch(int index) => random(PATCHES[index].Width, PATCHES[index].Height, 100 + index);

        public static Image<Rgba32> CreateFinal()
        {
            var final = CreateBase();

            for (int i = 0; i < PATCHES.Length; i++)
            {
                var patch = CreatePatch(i);
                var bounds = PATCHES[i];

                for (int y = 0; y < bounds.Height; y++)
                {
                    for (int x = 0; x < bounds.Width; x++)
                        final[bounds.X + x, bounds.Y + y] = patch[x, y];
                }
            }

            return final;
        }

        public static TextureUpload Upload(Image<Rgba32> image, RectangleI bounds) => new TextureUpload(image) { Bounds = bounds };

        /// <summary>
        /// Uploads every patch in one batch.
        /// </summary>
        public static void UploadPatches(Texture texture)
        {
            for (int i = 0; i < PATCHES.Length; i++)
                texture.SetData(Upload(CreatePatch(i), PATCHES[i]));
        }

        public static void AssertLevelsEqual(IReadOnlyList<Rgba32[]> actual, IReadOnlyList<Rgba32[]> expected)
        {
            Assert.That(actual, Has.Count.EqualTo(IRenderer.MAX_MIPMAP_LEVELS + 1));

            for (int level = 0; level < actual.Count; level++)
            {
                int differing = 0;
                int maxDifference = 0;

                for (int i = 0; i < actual[level].Length; i++)
                {
                    Rgba32 a = actual[level][i], e = expected[level][i];
                    int difference = Math.Max(Math.Max(Math.Abs(a.R - e.R), Math.Abs(a.G - e.G)), Math.Max(Math.Abs(a.B - e.B), Math.Abs(a.A - e.A)));

                    if (difference > 0)
                        differing++;

                    maxDifference = Math.Max(maxDifference, difference);
                }

                TestContext.Progress.WriteLine($"level {level}: {differing} differing texels, max difference {maxDifference}");
                Assert.That(differing, Is.Zero, $"level {level}");
            }
        }

        private static Image<Rgba32> random(int width, int height, int seed)
        {
            var rng = new Random(seed);
            var image = new Image<Rgba32>(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    image[x, y] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            }

            return image;
        }
    }
}
