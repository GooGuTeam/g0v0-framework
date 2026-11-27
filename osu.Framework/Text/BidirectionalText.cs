// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Text;

namespace osu.Framework.Text
{
    /// <summary>
    /// Splits a line of text into runs of a single direction, in the order they are drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shaping handles one direction at a time: HarfBuzz returns glyphs in visual order for the direction it is given,
    /// and putting a line that mixes directions into visual order is a separate step, described by UAX #9. This
    /// implements a subset of that algorithm: the paragraph direction comes from the first strong character, a
    /// character that is neither strongly left-to-right nor strongly right-to-left takes the direction of the text
    /// before it, and an entire run of a right-to-left paragraph is placed from right to left.
    /// </para>
    /// <para>
    /// Explicit embedding controls, bracket pairing and the full weak-type resolution rules are not implemented, so
    /// text relying on them is laid out by the simplified rules above.
    /// </para>
    /// </remarks>
    internal static class BidirectionalText
    {
        /// <summary>
        /// A span of text that is drawn in one direction.
        /// </summary>
        internal readonly struct TextRun
        {
            /// <summary>
            /// The index of the first UTF-16 code unit of the run.
            /// </summary>
            public readonly int Start;

            /// <summary>
            /// The number of UTF-16 code units in the run.
            /// </summary>
            public readonly int Length;

            /// <summary>
            /// Whether the run reads right to left.
            /// </summary>
            public readonly bool RightToLeft;

            public TextRun(int start, int length, bool rightToLeft)
            {
                Start = start;
                Length = length;
                RightToLeft = rightToLeft;
            }
        }

        /// <summary>
        /// Whether <paramref name="text"/> contains characters that read right to left.
        /// </summary>
        internal static bool ContainsRightToLeft(string text)
        {
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (getDirection(rune) == true)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Splits <paramref name="text"/> into runs, in the order their glyphs should be placed.
        /// </summary>
        internal static List<TextRun> GetRuns(string text)
        {
            var runs = new List<TextRun>();

            if (text.Length == 0)
                return runs;

            bool paragraphRightToLeft = paragraphIsRightToLeft(text);

            int runStart = 0;
            int position = 0;
            bool? runRightToLeft = null;

            // Enumerating runes rather than reading them one at a time is what makes an unpaired surrogate safe: it
            // is reported as U+FFFD instead of throwing, which is also what the per-character path draws for it.
            foreach (Rune rune in text.EnumerateRunes())
            {
                int i = position;

                // A character with no direction of its own reads in the direction of the text before it, or of the
                // paragraph when nothing precedes it.
                bool rightToLeft = getDirection(rune) ?? runRightToLeft ?? paragraphRightToLeft;

                if (runRightToLeft == null)
                {
                    runRightToLeft = rightToLeft;
                    runStart = i;
                }
                else if (runRightToLeft != rightToLeft)
                {
                    runs.Add(new TextRun(runStart, i - runStart, runRightToLeft.Value));
                    runRightToLeft = rightToLeft;
                    runStart = i;
                }

                position += rune.Utf16SequenceLength;
            }

            runs.Add(new TextRun(runStart, text.Length - runStart, runRightToLeft!.Value));

            // In a right-to-left paragraph the first run read is drawn furthest right, so the runs themselves are
            // placed in reverse. Each run is already shaped in visual order, so only their order changes here.
            if (paragraphRightToLeft)
                runs.Reverse();

            return runs;
        }

        /// <summary>
        /// UAX #9 P2/P3: the paragraph reads right to left if its first strong character does.
        /// </summary>
        /// <remarks>
        /// Digits are not strong: a line that starts with digits has no direction of its own until a letter appears.
        /// </remarks>
        private static bool paragraphIsRightToLeft(string text)
        {
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (Rune.IsDigit(rune))
                    continue;

                bool? direction = getDirection(rune);

                if (direction != null)
                    return direction.Value;
            }

            return false;
        }

        /// <summary>
        /// The direction of <paramref name="rune"/>, or <see langword="null"/> if it has none of its own.
        /// </summary>
        private static bool? getDirection(Rune rune)
        {
            // Digits read left to right even when the text around them does not.
            if (Rune.IsDigit(rune))
                return false;

            if (isRightToLeft(rune))
                return true;

            if (Rune.IsLetter(rune))
                return false;

            return null;
        }

        /// <summary>
        /// Whether <paramref name="rune"/> is a strongly right-to-left character.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The blocks that carry right-to-left scripts: Hebrew, Arabic and the scripts that share their direction, plus
        /// the presentation forms. Cypriot is left out of the supplementary range even though it sits next to the
        /// Aramaic-derived scripts, because it reads left to right.
        /// </para>
        /// <para>
        /// Only letters count. The same blocks also hold punctuation, marks and signs whose bidi classes are weak or
        /// neutral: U+060C ARABIC COMMA is a separator, so on its own it must not decide the direction of a
        /// paragraph, and a combining mark has no direction of its own at all. Digits are excluded for the same
        /// reason as everywhere else in this class: they read left to right even inside right-to-left text.
        /// </para>
        /// </remarks>
        private static bool isRightToLeft(Rune rune)
            => Rune.IsLetter(rune) && rune.Value is (>= 0x0590 and <= 0x08FF)
                or (>= 0xFB1D and <= 0xFDFF)
                or (>= 0xFE70 and <= 0xFEFF)
                or (>= 0x10840 and <= 0x10FFF)
                or (>= 0x1E800 and <= 0x1EFFF);
    }
}
