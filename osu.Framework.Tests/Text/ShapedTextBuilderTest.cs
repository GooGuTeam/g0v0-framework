// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
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

        /// <summary>
        /// A shaped cluster is drawn as a unit: two glyphs for "e" with two marks cannot be separated, because
        /// removing one of the glyphs would leave a mark without a base (or a base without its marks).
        /// </summary>
        [Test]
        public void TestRemoveLastCharacterRemovesWholeCluster()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);
            var builder = new TextBuilder(store, font);

            // The first acute composes into the base, the second mark stays a glyph of its own.
            builder.AddText("e\u0301\u0302");

            Assert.That(builder.Characters, Has.Count.EqualTo(2), "The remaining mark is shaped as a second glyph of the same cluster.");

            builder.RemoveLastCharacter();

            Assert.That(builder.Characters, Is.Empty, "Removing one character must not leave half a cluster behind.");
        }

        [Test]
        public void TestLegacyRemoveLastCharacterRemovesSingleGlyph()
        {
            using var store = createStore(TextShapingMode.Legacy);
            var builder = new TextBuilder(store, font);

            builder.AddText("ab");
            Assert.That(builder.Characters, Has.Count.EqualTo(2));

            builder.RemoveLastCharacter();

            Assert.That(builder.Characters, Has.Count.EqualTo(1), "Without clusters each glyph is still one character.");
        }

        /// <summary>
        /// Truncation must drop a cluster whole. The width here fits everything up to the multi-glyph cluster that
        /// overflowed it, which is exactly the case that would leave a bare base or a floating mark behind.
        /// </summary>
        [Test]
        public void TestTruncationDoesNotSplitCluster()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);

            const string prefix = "abc";
            const string cluster = "e\u0301\u0302";

            var measure = new TextBuilder(store, font);
            measure.AddText(prefix);
            float prefixWidth = measure.Bounds.X;

            var truncating = new TruncatingTextBuilder(store, font, prefixWidth + 1);
            truncating.AddText(prefix + cluster);

            Assert.That(truncating.Characters.Select(c => c.Character), Is.EqualTo(prefix), "The overflowing cluster is dropped rather than half drawn.");
            Assert.That(truncating.IsTruncated, Is.True);
        }

        /// <summary>
        /// The surviving prefix is shaped on its own and followed by the ellipsis, and everything stays within the
        /// width the caller asked for.
        /// </summary>
        [Test]
        public void TestTruncationKeepsPrefixAndAppendsEllipsis()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);

            var measure = new TextBuilder(store, font);
            measure.AddText("abcdef");
            float maxWidth = measure.Bounds.X * 0.6f;

            var truncating = new TruncatingTextBuilder(store, font, maxWidth, "...");
            truncating.AddText("abcdef");

            var glyphs = truncating.Characters;
            int prefixLength = glyphs.Count - 3;

            Assert.That(truncating.IsTruncated, Is.True);
            Assert.That(prefixLength, Is.GreaterThan(0).And.LessThan(6), "Some, but not all, of the text should survive.");
            Assert.That(glyphs.Take(prefixLength).Select(g => g.Character), Is.EqualTo("abcdef".Take(prefixLength)), "The prefix must be the start of the text, in order.");
            Assert.That(glyphs.Skip(prefixLength).Select(g => g.Character), Is.All.EqualTo('.'), "The ellipsis must follow the prefix.");
            Assert.That(truncating.Bounds.X, Is.LessThanOrEqualTo(maxWidth + 0.01f));
        }

        /// <summary>
        /// The kept prefix is shaped again on its own. Otherwise its last character would keep the form and the spacing
        /// it took while the text that was cut away still followed it.
        /// </summary>
        [Test]
        public void TestTruncatedPrefixIsShapedOnItsOwn()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);

            var pair = new TextBuilder(store, font);
            pair.AddText("AV");

            var longer = new TextBuilder(store, font);
            longer.AddText("AVA");

            float pairWidth = pair.Bounds.X;
            float vAdvanceAtEnd = pair.Characters[1].XAdvance;

            Assert.That(longer.Characters[1].XAdvance, Is.Not.EqualTo(vAdvanceAtEnd).Within(0.01f), "The font kerns V against the character that follows it.");

            var truncating = new TruncatingTextBuilder(store, font, pairWidth + 0.5f);
            truncating.AddText("AVA");

            Assert.That(truncating.Characters.Select(c => c.Character), Is.EqualTo("AV"));
            Assert.That(truncating.Characters[1].XAdvance, Is.EqualTo(vAdvanceAtEnd).Within(0.01f),
                "The last kept character must not be kerned against the text that was cut away.");
        }

        [Test]
        public void TestTextThatFitsIsNotTruncated()
        {
            using var store = createStore(TextShapingMode.HarfBuzz);

            var measure = new TextBuilder(store, font);
            measure.AddText("a");
            float charWidth = measure.Bounds.X;

            var truncating = new TruncatingTextBuilder(store, font, charWidth * 10, "...");
            truncating.AddText("abc");

            Assert.That(truncating.IsTruncated, Is.False);
            Assert.That(truncating.Characters, Has.Count.EqualTo(3));
        }

        private FontStore createStore(TextShapingMode mode)
        {
            var renderer = new DummyRenderer { ShapingMode = mode };
            return new FontStore(renderer, new TrueTypeGlyphStore(fontResourceStore, "Figtree-Regular"), scaleAdjust: 100);
        }
    }
}
