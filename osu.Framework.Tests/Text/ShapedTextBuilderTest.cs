// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Sprites;
using osu.Framework.IO.Stores;
using osu.Framework.Text;

namespace osu.Framework.Tests.Text
{
    /// <summary>
    /// Checks the shaped layout path end to end: HarfBuzz through <see cref="TrueTypeGlyphStore"/>, glyph indices
    /// through <see cref="FontStore"/>'s texture atlas, and into <see cref="TextBuilder"/>.
    /// </summary>
    [TestFixture]
    public class ShapedTextBuilderTest
    {
        private ResourceStore<byte[]> fontResourceStore = null!;

        private static readonly FontUsage font = new FontUsage("Figtree-Regular", weight: "Regular", size: 100);

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            fontResourceStore = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(ShapedTextBuilderTest).Assembly), "Resources/Fonts/Figtree");
        }

        /// <summary>
        /// The case the per-character path cannot do correctly: a base character and its combining mark are one glyph
        /// after shaping, and one code unit after normalisation, but two lookups without either.
        /// </summary>
        [Test]
        public void TestCombiningMarkShapesToSingleGlyph()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);
            var builder = new TextBuilder(store, font);

            builder.AddText("e\u0301");

            Assert.That(builder.Characters, Has.Count.EqualTo(1));
            Assert.That(builder.Characters[0].Texture, Is.Not.Null);
            Assert.That(builder.Characters[0].XAdvance, Is.GreaterThan(0));
        }

        [Test]
        public void TestLegacyModeIsUnchanged()
        {
            using var store = createStore(TextShapingMode.Legacy);
            var builder = new TextBuilder(store, font);

            builder.AddText("e\u0301");

            Assert.That(builder.Characters, Has.Count.EqualTo(2), "Without shaping, each code unit is still its own glyph.");
        }

        /// <summary>
        /// A run is shaped with a single font, so a character outside the font's coverage must send the whole run back
        /// to the per-character path rather than producing a <c>.notdef</c> box.
        /// </summary>
        [Test]
        public void TestUncoveredRunFallsBackToPerCharacterLookup()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);
            var builder = new TextBuilder(store, font);

            // Hiragana A is not in Figtree, so the run cannot be shaped and every character is looked up on its own.
            builder.AddText("a\u3042");

            Assert.That(builder.Characters, Has.Count.EqualTo(2));
            Assert.That(builder.Characters[0].Character, Is.EqualTo('a'));
            Assert.That(builder.Characters[1].Character, Is.EqualTo('?'), "The missing character should reach the fallback glyph.");
        }

        /// <summary>
        /// Glyphs are identified by index once shaped, so the texture has to be fetched by index too - and at the
        /// resolution the drawn size asks for, like the character-keyed path does.
        /// </summary>
        [Test]
        public void TestShapedGlyphUsesResolutionTier()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);

            // A single shaped glyph keeps the assertion honest: if the glyph-index texture lookup failed, shaping would
            // fall back to the character path and this would see two glyphs.
            var normal = new TextBuilder(store, font);
            normal.AddText("e\u0301");

            var large = new TextBuilder(store, new FontUsage("Figtree-Regular", weight: "Regular", size: 300));
            large.AddText("e\u0301");

            Assert.That(normal.Characters, Has.Count.EqualTo(1));
            Assert.That(large.Characters, Has.Count.EqualTo(1));

            Assert.That(large.Characters[0].Texture.Width, Is.EqualTo(normal.Characters[0].Texture.Width * 4).Within(4));
            Assert.That(large.Characters[0].XAdvance, Is.EqualTo(normal.Characters[0].XAdvance * 3).Within(0.5f), "Metrics are drawn size, not texture resolution.");
        }

        /// <summary>
        /// Shaping and per-character lookup describe the same glyph, so their metrics must agree - this is what catches
        /// a wrong conversion between font units and pixels.
        /// </summary>
        [Test]
        public void TestShapedMetricsMatchPerCharacterMetrics()
        {
            using var shapedStore = createStore(TextShapingMode.HarfBuzz);
            using var legacyStore = createStore(TextShapingMode.Legacy);

            var shaped = new TextBuilder(shapedStore, font);
            shaped.AddText("H");

            var legacy = new TextBuilder(legacyStore, font);
            legacy.AddText("H");

            Assert.That(shaped.Characters, Has.Count.EqualTo(1));
            Assert.That(legacy.Characters, Has.Count.EqualTo(1));

            Assert.That(shaped.Characters[0].XAdvance, Is.EqualTo(legacy.Characters[0].XAdvance).Within(0.01f));
            Assert.That(shaped.Characters[0].XOffset, Is.EqualTo(legacy.Characters[0].XOffset).Within(0.01f));
            Assert.That(shaped.Characters[0].YOffset, Is.EqualTo(legacy.Characters[0].YOffset).Within(0.01f));
            Assert.That(shaped.Characters[0].Baseline, Is.EqualTo(legacy.Characters[0].Baseline).Within(0.01f));
            Assert.That(shaped.Characters[0].Width, Is.EqualTo(legacy.Characters[0].Width).Within(0.01f));
        }

        private FontStore createStore(TextShapingMode mode)
        {
            var renderer = new DummyRenderer { ShapingMode = mode };
            return new FontStore(renderer, new TrueTypeGlyphStore(fontResourceStore, "Figtree-Regular"), scaleAdjust: 100);
        }
    }
}
