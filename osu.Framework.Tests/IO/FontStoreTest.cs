// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.IO;
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
        private TemporaryNativeStorage storage;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            storage = new TemporaryNativeStorage("fontstore-test");
            fontResourceStore = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Drawable).Assembly), "Resources.Fonts.Roboto");
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
        public void TestMissingHighDefinitionCompanionUsesOriginalGlyphs()
        {
            using var glyphStore = new RawCachingGlyphStore(fontResourceStore, "Roboto-Regular") { CacheStorage = storage };
            glyphStore.LoadFontAsync().GetAwaiter().GetResult();
            Assert.That(glyphStore.GetTextureScale('a'), Is.EqualTo(1));
            Assert.That(glyphStore.GetTextureScale('中'), Is.EqualTo(1));
            using var upload = glyphStore.Get("Roboto-Regular/a");
            Assert.That(upload, Is.Not.Null);
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
        public void TestHighDefinitionLoadsOnlyAboveNativeSize()
        {
            using var resources = new CompanionResources(fontResourceStore);
            using var fonts = new FontStore(new DummyRenderer(), new GlyphStore(resources, "Roboto-Regular"), scaleAdjust: 100);
            var normal = fonts.GetForSize("Roboto-Regular", 'a', 100);
            Assert.That(resources.CompanionRequests, Is.Zero);
            var high = fonts.GetForSize("Roboto-Regular", 'a', 101);
            Assert.That(resources.CompanionRequests, Is.GreaterThan(0));
            Assert.That(high.Width, Is.EqualTo(normal.Width / 2));
            Assert.That(high.XAdvance, Is.EqualTo(normal.XAdvance));
            Assert.That(high.Baseline, Is.EqualTo(normal.Baseline));
            Assert.That(fonts.GetForSize("Roboto-Regular", 'a', 50), Is.SameAs(normal));
            Assert.That(fonts.GetForSize("Roboto-Regular", 'a', 200), Is.SameAs(high));
        }

        [Test]
        public void TestNestedFontUsesItsOwnResolutionThreshold()
        {
            using var resources = new CompanionResources(fontResourceStore);
            using var fonts = new FontStore(new DummyRenderer(), scaleAdjust: 100, useAtlas: false);
            using var nested = new FontStore(new DummyRenderer(), new GlyphStore(resources, "Roboto-Regular"), scaleAdjust: 50);
            fonts.AddStore(nested);
            var normal = fonts.GetForSize("Roboto-Regular", 'a', 50);
            var high = fonts.GetForSize("Roboto-Regular", 'a', 51);
            Assert.That(high.Width, Is.EqualTo(normal.Width / 2));
            Assert.That(fonts.GetForSize("Roboto-Regular", 'a', 50), Is.SameAs(normal));
        }

        // Reuse the bitmap while doubling the declared resolution to test selection independently of rendering.
        private class CompanionResources : ResourceStore<byte[]>, IResourceStore<byte[]>
        {
            public int CompanionRequests { get; private set; }

            public CompanionResources(IResourceStore<byte[]> source)
                : base(source)
            {
            }

            Stream IResourceStore<byte[]>.GetStream(string name)
            {
                if (!name.Contains("-ASCII-HD", StringComparison.Ordinal))
                    return GetStream(name);

                CompanionRequests++;
                using var original = GetStream(name.Replace("-ASCII-HD", string.Empty, StringComparison.Ordinal));
                if (original == null)
                    return null;

                using var result = new MemoryStream();
                original.CopyTo(result);
                byte[] bytes = result.ToArray();
                if (name.EndsWith(".bin", StringComparison.Ordinal))
                {
                    // BMFont common block starts with lineHeight (uint16), after a four-byte file header.
                    for (int offset = 4; offset < bytes.Length; offset += 5 + BitConverter.ToInt32(bytes, offset + 1))
                    {
                        if (bytes[offset] != 2)
                            continue;

                        ushort height = BitConverter.ToUInt16(bytes, offset + 5);
                        BitConverter.GetBytes((ushort)(height * 2)).CopyTo(bytes, offset + 5);
                        break;
                    }
                }

                return new MemoryStream(bytes);
            }
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
