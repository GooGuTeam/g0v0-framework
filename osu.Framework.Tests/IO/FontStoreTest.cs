// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering.Dummy;
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
