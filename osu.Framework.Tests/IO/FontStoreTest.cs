// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using NUnit.Framework;
using SharpFNT;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Textures;
using osu.Framework.Imaging;
using osu.Framework.IO.Stores;
using osu.Framework.Testing;
using osu.Framework.Text;

namespace osu.Framework.Tests.IO
{
    [TestFixture]
    public class FontStoreTest
    {
        private ResourceStore<byte[]> fontResourceStore;
        private ResourceStore<byte[]> trueTypeResourceStore;
        private TemporaryNativeStorage storage;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            storage = new TemporaryNativeStorage("fontstore-test");
            fontResourceStore = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Drawable).Assembly), "Resources.Fonts.Roboto");
            trueTypeResourceStore = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(FontStoreTest).Assembly), "Resources/Fonts/Figtree");
        }

        [Test]
        public void TestNestedScaleAdjust()
        {
            using (var fontStore = new FontStore(new DummyRenderer(), new RawCachingGlyphStore(fontResourceStore, "Roboto-Regular") { CacheStorage = storage }, scaleAdjust: 100))
            using (var nestedFontStore = new FontStore(new DummyRenderer(), new RawCachingGlyphStore(fontResourceStore, "Roboto-Bold") { CacheStorage = storage }, 10))
            {
                fontStore.AddStore(nestedFontStore);

                var normalGlyph = (TexturedCharacterGlyph)fontStore.Get("Roboto-Regular", 'a');
                Assert.That(normalGlyph, Is.Not.Null);

                var boldGlyph = (TexturedCharacterGlyph)fontStore.Get("Roboto-Bold", 'a');
                Assert.That(boldGlyph, Is.Not.Null);

                Assert.That(normalGlyph.Scale, Is.EqualTo(1f / 100));
                Assert.That(boldGlyph.Scale, Is.EqualTo(1f / 10));
            }
        }

        [Test]
        public void TestHighDefinitionTexturePreservesMetrics()
        {
            using var fontStore = new FontStore(new DummyRenderer(), new RawCachingGlyphStore(fontResourceStore, "Roboto-Regular") { CacheStorage = storage }, scaleAdjust: 100);
            var original = fontStore.Get("Roboto-Regular", 'a');
            var metrics = new CharacterGlyph('a', 4, 6, 50, 75, null);
            var normal = new TexturedCharacterGlyph(metrics, original.Texture, 0.01f);
            var highDefinition = new TexturedCharacterGlyph(metrics, original.Texture, 0.01f, 4);

            Assert.That(highDefinition.Width, Is.EqualTo(normal.Width / 4));
            Assert.That(highDefinition.Height, Is.EqualTo(normal.Height / 4));
            Assert.That(highDefinition.XOffset, Is.EqualTo(normal.XOffset));
            Assert.That(highDefinition.YOffset, Is.EqualTo(normal.YOffset));
            Assert.That(highDefinition.XAdvance, Is.EqualTo(normal.XAdvance));
            Assert.That(highDefinition.Baseline, Is.EqualTo(normal.Baseline));
        }

        [Test]
        public void TestGlyphResourceNamespaceMatching()
        {
            using var glyphStore = new GlyphStore(fontResourceStore, "Roboto-Regular");
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();

            using var validUpload = glyphStore.Get("Roboto-Regular/a");
            using var invalidUpload = glyphStore.Get("Roboto-Regular-extra/a");

            Assert.That(validUpload, Is.Not.Null);
            Assert.That(invalidUpload, Is.Null);
        }

        [Test]
        public void TestNormalResolutionGlyphCopiesTrimmedCoverageAndCountsOnce()
        {
            using var glyphStore = new TestableGlyphStore(fontResourceStore, "Roboto-Regular");
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();

            Character character = glyphStore.GetCharacter('W');
            int readableWidth = Math.Min(3, character.Width - 1);
            int readableHeight = Math.Min(2, character.Height - 1);
            Assert.That(readableWidth, Is.GreaterThan(0));
            Assert.That(readableHeight, Is.GreaterThan(0));

            int pageWidth = character.X + readableWidth;
            int pageHeight = character.Y + readableHeight;
            using var pageImage = new Image<Rgba32>(pageWidth, pageHeight);
            byte[] sourceCoverage = { 16, 32, 48, 64, 80, 96 };
            var pixels = pageImage.Pixels;

            for (int y = 0; y < readableHeight; y++)
            {
                for (int x = 0; x < readableWidth; x++)
                    pixels[(character.Y + y) * pageWidth + character.X + x] = new Rgba32(0, 0, 0, sourceCoverage[y * 3 + x]);
            }

            using var pageUpload = new TextureUpload(pageImage);
            glyphStore.Page = pageUpload;
            using var result = (AlphaTextureUpload)glyphStore.LoadGlyph(character);

            Assert.That(result.Width, Is.EqualTo(character.Width));
            Assert.That(result.Height, Is.EqualTo(character.Height));
            Assert.That(result.RawData.Length, Is.EqualTo(character.Width * character.Height));
            Assert.That(glyphStore.LoadedCount, Is.EqualTo(1));

            for (int y = 0; y < character.Height; y++)
            {
                for (int x = 0; x < character.Width; x++)
                {
                    byte expected = x < readableWidth && y < readableHeight ? sourceCoverage[y * 3 + x] : (byte)0;
                    Assert.That(result.RawData[y * character.Width + x], Is.EqualTo(expected));
                }
            }
        }

        [Test]
        public void TestLowResolutionGlyphAveragesCoverageAndCountsOnce()
        {
            using var glyphStore = new TestableGlyphStore(fontResourceStore, "Roboto-Regular") { LowResolutionTextures = true };
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();

            Character character = glyphStore.GetCharacter('W');
            int pageWidth = character.X + character.Width;
            int pageHeight = character.Y + character.Height;
            using var pageImage = new Image<Rgba32>(pageWidth, pageHeight, new Rgba32(0, 0, 0, 100));
            var pixels = pageImage.Pixels;
            pixels[character.Y * pageWidth + character.X] = new Rgba32(0, 0, 0, 10);
            pixels[character.Y * pageWidth + character.X + 1] = new Rgba32(0, 0, 0, 20);
            pixels[(character.Y + 1) * pageWidth + character.X] = new Rgba32(0, 0, 0, 30);
            pixels[(character.Y + 1) * pageWidth + character.X + 1] = new Rgba32(0, 0, 0, 40);

            using var pageUpload = new TextureUpload(pageImage);
            glyphStore.Page = pageUpload;
            using var result = (AlphaTextureUpload)glyphStore.LoadGlyph(character);

            Assert.That(result.Width, Is.EqualTo((character.Width + 1) / 2));
            Assert.That(result.Height, Is.EqualTo((character.Height + 1) / 2));
            Assert.That(result.RawData[0], Is.EqualTo(25));
            Assert.That(result.RawData[^1], Is.EqualTo(100));
            Assert.That(glyphStore.LoadedCount, Is.EqualTo(1));
        }

        private class TestableGlyphStore : GlyphStore
        {
            public TextureUpload Page { get; set; }
            public int LoadedCount => LoadedGlyphCount;

            public TestableGlyphStore(ResourceStore<byte[]> store, string assetName)
                : base(store, assetName)
            {
            }

            public Character GetCharacter(char character) => Font!.Characters[character];
            public TextureUpload LoadGlyph(Character character) => LoadCharacter(character);

            protected override TextureUpload GetPageImage(int page) => Page;
        }

        [Test]
        public void TestTrueTypeMetricsUseCellHeight()
        {
            using var glyphStore = new TrueTypeGlyphStore(trueTypeResourceStore, "Figtree-Regular");
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();

            // Figtree's hhea ascender/descender are 950/-250, so the baseline sits at 950/1200 of a 100px cell.
            Assert.That(glyphStore.Baseline, Is.EqualTo(100 * 950f / 1200).Within(0.01f));
            Assert.That(glyphStore.HasGlyph('A'), Is.True);
            Assert.That(glyphStore.HasGlyph('中'), Is.False);

            var glyph = glyphStore.Get('H');
            Assert.That(glyph, Is.Not.Null);
            Assert.That(glyph!.XAdvance, Is.GreaterThan(0));
            Assert.That(glyph.YOffset, Is.GreaterThan(0), "a cap-height glyph starts below the top of the cell");

            using var texture = glyphStore.Get("Figtree-Regular/H");
            Assert.That(texture, Is.Not.Null);
            Assert.That(glyph.YOffset + texture.Height, Is.EqualTo(glyphStore.Baseline!.Value).Within(1), "an 'H' sits on the baseline");
        }

        [Test]
        public void TestTrueTypeKerningFromGpos()
        {
            using var glyphStore = new TrueTypeGlyphStore(trueTypeResourceStore, "Figtree-Regular");
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();

            Assert.That(glyphStore.GetKerning('A', 'V'), Is.LessThan(0));
            Assert.That(glyphStore.GetKerning('T', 'o'), Is.LessThan(0));
            Assert.That(glyphStore.GetKerning('H', 'H'), Is.Zero);
        }

        [Test]
        public void TestTrueTypeResolutionTiers()
        {
            using var glyphStore = new TrueTypeGlyphStore(trueTypeResourceStore, "Figtree-Regular");
            using var fonts = new FontStore(new DummyRenderer(), glyphStore, scaleAdjust: 100);

            var normal = fonts.GetForSize("Figtree-Regular", 'a', 100);
            var doubled = fonts.GetForSize("Figtree-Regular", 'a', 101);
            var quadrupled = fonts.GetForSize("Figtree-Regular", 'a', 300);

            Assert.That(normal, Is.Not.Null);
            Assert.That(doubled.Texture.Width, Is.EqualTo(normal.Texture.Width * 2).Within(2));
            Assert.That(quadrupled.Texture.Width, Is.EqualTo(normal.Texture.Width * 4).Within(4));

            // Higher resolution textures keep the glyph's size and metrics.
            Assert.That(doubled.Width, Is.EqualTo(normal.Width).Within(0.02f));
            Assert.That(quadrupled.Width, Is.EqualTo(normal.Width).Within(0.02f));
            Assert.That(doubled.XAdvance, Is.EqualTo(normal.XAdvance));
            Assert.That(quadrupled.Baseline, Is.EqualTo(normal.Baseline));

            Assert.That(fonts.GetForSize("Figtree-Regular", 'a', 50), Is.SameAs(normal));
            Assert.That(fonts.GetForSize("Figtree-Regular", 'a', 200), Is.SameAs(doubled));
        }

        [Test]
        public void TestTrueTypeLowResolution()
        {
            using var glyphStore = new TrueTypeGlyphStore(trueTypeResourceStore, "Figtree-Regular") { LowResolutionTextures = true };
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();

            Assert.That(glyphStore.GetTextureScale('a', 4), Is.EqualTo(0.5f));

            using var full = new TrueTypeGlyphStore(trueTypeResourceStore, "Figtree-Regular");
            full.LoadFontAsync().GetAwaiter().GetResult();

            using var half = glyphStore.Get(glyphStore.GetTextureName('W', 1));
            using var original = full.Get("Figtree-Regular/W");
            Assert.That(half.Width, Is.EqualTo(original.Width / 2).Within(1));
        }

        [Test]
        public void TestTrueTypeTextureNames()
        {
            using var glyphStore = new TrueTypeGlyphStore(trueTypeResourceStore, "Figtree-Regular");
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();

            Assert.That(glyphStore.GetTextureName('a', 1), Is.EqualTo("Figtree-Regular/a"));
            Assert.That(glyphStore.GetTextureName('a', 2), Is.EqualTo("Figtree-Regular/@2x/a"));

            using var doubled = glyphStore.Get("Figtree-Regular/@2x/a");
            using var otherFont = glyphStore.Get("Figtree-Bold/a");
            using var malformed = glyphStore.Get("Figtree-Regular/@x/a");

            Assert.That(doubled, Is.Not.Null);
            Assert.That(otherFont, Is.Null);
            Assert.That(malformed, Is.Null);
        }

        [Test]
        public void TestCreateGlyphStorePrefersTrueType()
        {
            using var trueType = Game.CreateGlyphStore(trueTypeResourceStore, "Figtree-Regular");
            using var bitmap = Game.CreateGlyphStore(fontResourceStore, "Roboto-Regular");

            Assert.That(trueType, Is.InstanceOf<TrueTypeGlyphStore>());
            Assert.That(bitmap, Is.InstanceOf<RawCachingGlyphStore>());
        }

        [Test]
        public void TestNoCrashOnMissingResources()
        {
            using (var glyphStore = new RawCachingGlyphStore(fontResourceStore, "DoesntExist"))
            {
                glyphStore.CacheStorage = storage;

                using (var fontStore = new FontStore(new DummyRenderer(), glyphStore, 100))
                {
                    Assert.That(glyphStore.Get('a'), Is.Null);

                    Assert.That(fontStore.Get("DoesntExist", 'a'), Is.Null);
                    Assert.That(fontStore.Get("OtherAttempt", 'a'), Is.Null);
                }
            }
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            storage.Dispose();
        }
    }
}
