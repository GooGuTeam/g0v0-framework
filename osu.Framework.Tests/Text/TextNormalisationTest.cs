// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Text;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Sprites;
using osu.Framework.IO.Stores;
using osu.Framework.Text;

namespace osu.Framework.Tests.Text
{
    [TestFixture]
    public class TextNormalisationTest
    {
        private const string composed = "\u00e9";
        private const string decomposed = "e\u0301";

        [Test]
        public void TestDecomposedTextIsComposed()
        {
            Assert.That(TextNormalisation.Normalise(decomposed), Is.EqualTo(composed));
        }

        [Test]
        public void TestNormalisationIsIdempotent()
        {
            string once = TextNormalisation.Normalise(decomposed);

            Assert.That(TextNormalisation.Normalise(once), Is.EqualTo(once));
        }

        [Test]
        public void TestAlreadyNormalisedTextIsReturnedAsIs()
        {
            const string text = "unicode \u00e9 text";

            Assert.That(TextNormalisation.Normalise(text), Is.SameAs(text));
        }

        [Test]
        public void TestAsciiIsReturnedAsIs()
        {
            const string text = "plain ascii";

            Assert.That(TextNormalisation.Normalise(text), Is.SameAs(text));
        }

        [Test]
        public void TestEmptyTextIsReturnedAsIs()
        {
            string text = string.Empty;

            Assert.That(TextNormalisation.Normalise(text), Is.SameAs(text));
        }

        [Test]
        public void TestNewlinesArePreserved()
        {
            Assert.That(TextNormalisation.Normalise("e\u0301\nline"), Is.EqualTo("\u00e9\nline"));
        }

        /// <summary>
        /// A string which was cut in the middle of a surrogate pair cannot be normalised by the BCL at all - it throws
        /// rather than returning the input - so the render path must not let that escape.
        /// </summary>
        [Test]
        public void TestUnpairedSurrogateIsReturnedAsIs()
        {
            const string text = "a\ud83d";

            Assert.That(TextNormalisation.Normalise(text), Is.EqualTo(text));
        }

        /// <summary>
        /// The support flag is the only way callers can tell that normalisation silently did nothing, so it has to
        /// agree with what <see cref="string.Normalize(NormalizationForm)"/> actually does in this runtime.
        /// </summary>
        [Test]
        public void TestSupportFlagMatchesRuntimeBehaviour()
        {
            bool composes = decomposed.Normalize(NormalizationForm.FormC) == composed;

            Assert.That(TextNormalisation.IsSupported, Is.EqualTo(composes));
        }

        /// <summary>
        /// Pins the difference the render path cares about: the legacy builder sees one glyph per UTF-16 code unit, so
        /// a decomposed sequence costs an extra glyph until it is normalised.
        /// </summary>
        [Test]
        public void TestTextBuilderSeesSingleGlyphAfterNormalisation()
        {
            Assert.That(glyphCount(composed), Is.EqualTo(1), "A precomposed character is a single code unit.");
            Assert.That(glyphCount(decomposed), Is.EqualTo(2), "An unnormalised sequence reaches the builder as two code units.");
            Assert.That(glyphCount(TextNormalisation.Normalise(decomposed)), Is.EqualTo(1), "Normalised input reaches the builder as one code unit.");
        }

        /// <summary>
        /// Word splitting treats a CJK character as a break opportunity, so a kana followed by its combining voiced
        /// sound mark would be torn into two sprites if normalisation only happened inside each <see cref="SpriteText"/>.
        /// </summary>
        [Test]
        public void TestTextFlowChunkComposesBeforeSplitting()
        {
            // KA + COMBINING KATAKANA-HIRAGANA VOICED SOUND MARK, which composes to GA.
            var chunk = new TextChunk<SpriteText>("\u304b\u3099", true, () => new SpriteText());

            chunk.RecreateDrawablesFor(new TextFlowContainer());

            var sprites = chunk.Drawables.OfType<SpriteText>().ToArray();

            Assert.That(sprites, Has.Length.EqualTo(1));
            Assert.That(sprites[0].Text.ToString(), Is.EqualTo("\u304c"));
        }

        private static int glyphCount(string text)
        {
            var fontStore = new FontStore(new DummyRenderer(), useAtlas: false);
            fontStore.AddTextureSource(new GlyphStore(new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), @"Resources"), "Fonts/Roboto/Roboto-Regular"));

            var builder = new TextBuilder(fontStore, new FontUsage("Roboto", weight: "Regular", size: 1));
            builder.AddText(text);

            return builder.Characters.Count;
        }
    }
}
