// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Text;

namespace osu.Framework.Tests.Text
{
    /// <summary>
    /// Checks where a run of text is split before it is shaped, which decides whether each script reaches HarfBuzz
    /// with its own rules applied.
    /// </summary>
    [TestFixture]
    public class ScriptRunsTest
    {
        [Test]
        public void TestSingleScriptIsOneRun()
        {
            Assert.That(ScriptRuns.GetBoundaries("hello"), Is.EqualTo(new[] { 0, 5 }));
            Assert.That(ScriptRuns.GetBoundaries("مرحبا"), Is.EqualTo(new[] { 0, 5 }));
        }

        [Test]
        public void TestScriptChangeSplitsTheRun()
        {
            // "abc" + DEVANAGARI KA + DEVANAGARI VOWEL SIGN I, the vowel sign being what makes the syllable a cluster.
            Assert.That(ScriptRuns.GetBoundaries("abc\u0915\u093F"), Is.EqualTo(new[] { 0, 3, 5 }));
            Assert.That(ScriptRuns.GetBoundaries("a\u0915a"), Is.EqualTo(new[] { 0, 1, 2, 3 }));
        }

        /// <summary>
        /// A mark that attaches to the character before it must not start a run, or it would be shaped as the first
        /// character of a script that has nothing to attach it to.
        /// </summary>
        [Test]
        public void TestMarkStaysWithItsBase()
        {
            Assert.That(ScriptRuns.GetBoundaries("e\u0301"), Is.EqualTo(new[] { 0, 2 }));
            Assert.That(ScriptRuns.GetBoundaries("a\u0915\u093F"), Is.EqualTo(new[] { 0, 1, 3 }));
        }

        /// <summary>
        /// Spacing and punctuation have no script of their own and belong to the text before them, so a word break
        /// does not turn into a script break.
        /// </summary>
        [Test]
        public void TestNeutralCharactersBelongToThePrecedingScript()
        {
            Assert.That(ScriptRuns.GetBoundaries("abc \u0915\u093F"), Is.EqualTo(new[] { 0, 4, 6 }));
            Assert.That(ScriptRuns.GetBoundaries("\u060C"), Is.EqualTo(new[] { 0, 1 }), "A comma on its own belongs to no script.");
            Assert.That(ScriptRuns.GetBoundaries("abc 123"), Is.EqualTo(new[] { 0, 7 }));
        }

        /// <summary>
        /// Japanese text mixes kana and kanji within a sentence, so they share one run and one set of rules.
        /// </summary>
        [Test]
        public void TestKanaAndKanjiAreNotSplit()
        {
            Assert.That(ScriptRuns.GetBoundaries("あ漢"), Is.EqualTo(new[] { 0, 2 }));
        }

        /// <summary>
        /// Hangul is a script of its own, and a run that started with a Han character would otherwise compose its
        /// syllables with the wrong rules.
        /// </summary>
        [Test]
        public void TestHangulIsSplitFromHan()
        {
            Assert.That(ScriptRuns.GetBoundaries("漢글"), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void TestTextWithoutScriptIsOneRun()
        {
            Assert.That(ScriptRuns.GetBoundaries("123 ...,,,"), Is.EqualTo(new[] { 0, 10 }));
            Assert.That(ScriptRuns.GetBoundaries(string.Empty), Is.EqualTo(new[] { 0 }));
        }
    }
}
