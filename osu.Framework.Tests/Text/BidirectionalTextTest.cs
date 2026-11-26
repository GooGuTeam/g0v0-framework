// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Text;

namespace osu.Framework.Tests.Text
{
    /// <summary>
    /// Checks how a line is split into runs of a single direction before shaping, which is the part of UAX #9 that
    /// decides in which order the runs are drawn.
    /// </summary>
    [TestFixture]
    public class BidirectionalTextTest
    {
        private const string arabic = "مرحبا";

        [Test]
        public void TestLeftToRightTextIsOneRun()
        {
            var runs = BidirectionalText.GetRuns("hello");

            Assert.That(runs, Has.Count.EqualTo(1));
            Assert.That(runs[0].Start, Is.EqualTo(0));
            Assert.That(runs[0].Length, Is.EqualTo(5));
            Assert.That(runs[0].RightToLeft, Is.False);
        }

        [Test]
        public void TestRightToLeftTextIsOneRun()
        {
            var runs = BidirectionalText.GetRuns(arabic);

            Assert.That(runs, Has.Count.EqualTo(1));
            Assert.That(runs[0].RightToLeft, Is.True);
        }

        /// <summary>
        /// In a left-to-right paragraph the runs keep their logical order; the run that reads right to left is shaped
        /// the other way around by itself.
        /// </summary>
        [Test]
        public void TestEmbeddedRightToLeftKeepsOrder()
        {
            // "abc " + the Arabic word + " def". The space after the Arabic word belongs to it, because it takes the
            // direction of the text before it.
            var runs = BidirectionalText.GetRuns("abc " + arabic + " def");

            Assert.That(runs.Select(r => (r.Start, r.Length, r.RightToLeft)), Is.EqualTo(new[]
            {
                (0, 4, false),
                (4, 6, true),
                (10, 3, false),
            }));
        }

        /// <summary>
        /// In a right-to-left paragraph the first run read is drawn furthest right, so the runs are placed in reverse.
        /// </summary>
        [Test]
        public void TestRightToLeftParagraphIsReversed()
        {
            var runs = BidirectionalText.GetRuns(arabic + " abc");

            Assert.That(runs.Select(r => (r.Start, r.Length, r.RightToLeft)), Is.EqualTo(new[]
            {
                (6, 3, false),
                (0, 6, true),
            }));
        }

        /// <summary>
        /// Digits are not right-to-left, even inside right-to-left text: they are drawn in the order they are written.
        /// </summary>
        [Test]
        public void TestDigitsReadLeftToRight()
        {
            var runs = BidirectionalText.GetRuns(arabic + " 123");

            Assert.That(runs.Select(r => (r.Start, r.Length, r.RightToLeft)), Is.EqualTo(new[]
            {
                (6, 3, false),
                (0, 6, true),
            }));

            var arabicIndicDigits = BidirectionalText.GetRuns("\u0661\u0662\u0663");

            Assert.That(arabicIndicDigits, Has.Count.EqualTo(1));
            Assert.That(arabicIndicDigits[0].RightToLeft, Is.False);
            Assert.That(BidirectionalText.ContainsRightToLeft("\u0661\u0662\u0663"), Is.False);
        }

        [Test]
        public void TestContainsRightToLeft()
        {
            Assert.That(BidirectionalText.ContainsRightToLeft("hello"), Is.False);
            Assert.That(BidirectionalText.ContainsRightToLeft(arabic), Is.True);
            Assert.That(BidirectionalText.ContainsRightToLeft("abc " + arabic), Is.True);
        }

        [Test]
        public void TestEmptyTextHasNoRuns()
        {
            Assert.That(BidirectionalText.GetRuns(string.Empty), Is.Empty);
        }
    }
}
