// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using BenchmarkDotNet.Attributes;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Imaging;
using SharpFNT;

namespace osu.Framework.Benchmarks
{
    [MemoryDiagnoser]
    public class BenchmarkGlyphExtraction
    {
        private BenchmarkStore glyphStore = null!;
        private Character character = null!;
        private TextureUpload page = null!;

        [Params(false, true)]
        public bool LowResolutionTextures { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Drawable).Assembly), @"Resources.Fonts.Roboto");
            glyphStore = new BenchmarkStore(resources, "Roboto-Regular") { LowResolutionTextures = LowResolutionTextures };
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();
            character = glyphStore.GetCharacter('W');

            int pageWidth = character.X + character.Width;
            int pageHeight = character.Y + character.Height;
            var image = new Image<Rgba32>(pageWidth, pageHeight);
            var pixels = image.Pixels;

            for (int y = 0; y < pageHeight; y++)
            {
                for (int x = 0; x < pageWidth; x++)
                    pixels[y * pageWidth + x] = new Rgba32(0, 0, 0, (byte)((x * 31 + y * 17) & 255));
            }

            page = new TextureUpload(image);
            glyphStore.Page = page;
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            glyphStore.Dispose();
            page.Dispose();
        }

        [Benchmark(Baseline = true)]
        public byte PreviousGenericExtraction()
        {
            int scale = LowResolutionTextures ? 2 : 1;
            int width = (character.Width + scale - 1) / scale;
            int height = (character.Height + scale - 1) / scale;
            int readableHeight = System.Math.Min(character.Height, page.Height - character.Y);
            int readableWidth = System.Math.Min(character.Width, page.Width - character.X);
            var source = page.Data;
            byte[] coverage = new byte[checked(width * height)];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int total = 0;
                    int samples = 0;

                    for (int sy = 0; sy < scale; sy++)
                    {
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int sourceX = x * scale + sx;
                            int sourceY = y * scale + sy;
                            if (sourceX >= readableWidth || sourceY >= readableHeight)
                                continue;

                            total += source[(character.Y + sourceY) * page.Width + character.X + sourceX].A;
                            samples++;
                        }
                    }

                    coverage[y * width + x] = samples == 0 ? (byte)0 : (byte)((total + samples / 2) / samples);
                }
            }

            using var result = new AlphaTextureUpload(width, height, coverage);
            return result.RawData[0];
        }

        [Benchmark]
        public byte OptimizedExtraction()
        {
            using var result = glyphStore.LoadGlyph(character);
            return result.RawData[0];
        }

        private class BenchmarkStore : GlyphStore
        {
            public TextureUpload Page { get; set; } = null!;

            public BenchmarkStore(ResourceStore<byte[]> store, string assetName)
                : base(store, assetName)
            {
            }

            public Character GetCharacter(char character) => Font!.Characters[character];
            public TextureUpload LoadGlyph(Character character) => LoadCharacter(character);

            protected override TextureUpload GetPageImage(int page) => Page;
        }
    }
}
