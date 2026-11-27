// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Text;

namespace osu.Framework.Text
{
    /// <summary>
    /// Splits a run of text where its script changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HarfBuzz takes one script per buffer and applies that script's rules to everything in it, guessing the script
    /// from the first character that has one. Text that mixes scripts therefore reaches the shaper looking entirely
    /// like its first script, and the rest of it is shaped by rules that do not apply to it: a Devanagari syllable
    /// after a Latin word never forms its conjuncts, and the result is not a missing glyph that a fallback would
    /// notice, just text that looks wrong.
    /// </para>
    /// <para>
    /// Characters with no script of their own - spaces, punctuation, digits, combining marks - stay with the script
    /// around them, and in particular never start a run of their own, so a mark is never separated from the base it
    /// attaches to.
    /// </para>
    /// <para>
    /// Scripts are told apart only where the difference changes shaping. The CJK scripts share an identifier because
    /// they are mixed freely within one language and HarfBuzz treats them alike; scripts whose shaping does not differ
    /// from their neighbours are left out of the table and inherit the script before them.
    /// </para>
    /// </remarks>
    internal static class ScriptRuns
    {
        private enum Script
        {
            /// <summary>
            /// No script of its own: the character belongs to the script it is surrounded by.
            /// </summary>
            Common,

            Latin,
            Greek,
            Cyrillic,
            Armenian,
            Hebrew,
            Arabic,
            Syriac,
            Thaana,
            NKo,
            Devanagari,
            Bengali,
            Gurmukhi,
            Gujarati,
            Oriya,
            Tamil,
            Telugu,
            Kannada,
            Malayalam,
            Sinhala,
            Thai,
            Lao,
            Tibetan,
            Myanmar,
            Georgian,
            Hangul,
            Ethiopic,
            Cherokee,
            Khmer,
            Mongolian,

            /// <summary>
            /// Han, Hiragana, Katakana and Bopomofo, which are mixed within Japanese and Chinese text.
            /// </summary>
            Cjk,
        }

        /// <summary>
        /// The code point ranges that carry a script, sorted by their first code point.
        /// </summary>
        private static readonly (int Start, int End, Script Script)[] ranges =
        {
            (0x0041, 0x005A, Script.Latin),
            (0x0061, 0x007A, Script.Latin),
            (0x00C0, 0x024F, Script.Latin),
            (0x0370, 0x03FF, Script.Greek),
            (0x0400, 0x052F, Script.Cyrillic),
            (0x0530, 0x058F, Script.Armenian),
            (0x0590, 0x05FF, Script.Hebrew),
            (0x0600, 0x06FF, Script.Arabic),
            (0x0700, 0x074F, Script.Syriac),
            (0x0750, 0x077F, Script.Arabic),
            (0x0780, 0x07BF, Script.Thaana),
            (0x07C0, 0x07FF, Script.NKo),
            (0x0860, 0x086F, Script.Syriac),
            (0x08A0, 0x08FF, Script.Arabic),
            (0x0900, 0x097F, Script.Devanagari),
            (0x0980, 0x09FF, Script.Bengali),
            (0x0A00, 0x0A7F, Script.Gurmukhi),
            (0x0A80, 0x0AFF, Script.Gujarati),
            (0x0B00, 0x0B7F, Script.Oriya),
            (0x0B80, 0x0BFF, Script.Tamil),
            (0x0C00, 0x0C7F, Script.Telugu),
            (0x0C80, 0x0CFF, Script.Kannada),
            (0x0D00, 0x0D7F, Script.Malayalam),
            (0x0D80, 0x0DFF, Script.Sinhala),
            (0x0E00, 0x0E7F, Script.Thai),
            (0x0E80, 0x0EFF, Script.Lao),
            (0x0F00, 0x0FFF, Script.Tibetan),
            (0x1000, 0x109F, Script.Myanmar),
            (0x10A0, 0x10FF, Script.Georgian),
            (0x1100, 0x11FF, Script.Hangul),
            (0x1200, 0x139F, Script.Ethiopic),
            (0x13A0, 0x13FF, Script.Cherokee),
            (0x1780, 0x17FF, Script.Khmer),
            (0x1800, 0x18AF, Script.Mongolian),
            (0x19E0, 0x19FF, Script.Khmer),
            (0x1C80, 0x1C8F, Script.Cyrillic),
            (0x1C90, 0x1CBF, Script.Georgian),
            (0x1E00, 0x1EFF, Script.Latin),
            (0x1F00, 0x1FFF, Script.Greek),
            (0x20000, 0x2FA1F, Script.Cjk),
            (0x2C60, 0x2C7F, Script.Latin),
            (0x2DE0, 0x2DFF, Script.Cyrillic),
            (0x2E80, 0x2EFF, Script.Cjk),
            (0x3005, 0x3005, Script.Cjk),
            (0x3007, 0x3007, Script.Cjk),
            (0x3021, 0x3029, Script.Cjk),
            (0x3038, 0x303B, Script.Cjk),
            (0x3040, 0x30FF, Script.Cjk),
            (0x3100, 0x312F, Script.Cjk),
            (0x3130, 0x318F, Script.Hangul),
            (0x31A0, 0x31BF, Script.Cjk),
            (0x31F0, 0x31FF, Script.Cjk),
            (0x3400, 0x4DBF, Script.Cjk),
            (0x4E00, 0x9FFF, Script.Cjk),
            (0xA640, 0xA69F, Script.Cyrillic),
            (0xA720, 0xA7FF, Script.Latin),
            (0xA8E0, 0xA8FF, Script.Devanagari),
            (0xA960, 0xA97F, Script.Hangul),
            (0xAA60, 0xAA7F, Script.Myanmar),
            (0xAB30, 0xAB6F, Script.Latin),
            (0xAB70, 0xABBF, Script.Cherokee),
            (0xAC00, 0xD7AF, Script.Hangul),
            (0xF900, 0xFAFF, Script.Cjk),
            (0xFB1D, 0xFB4F, Script.Hebrew),
            (0xFB50, 0xFDFF, Script.Arabic),
            (0xFE70, 0xFEFF, Script.Arabic),
        };

        /// <summary>
        /// The offsets at which <paramref name="text"/> changes script, starting at 0 and ending at its length.
        /// </summary>
        /// <remarks>
        /// A run with no boundary in the middle describes a single script, which is the common case and the only one
        /// the shaper has to handle differently from shaping the text as it is.
        /// </remarks>
        internal static List<int> GetBoundaries(string text)
        {
            var boundaries = new List<int> { 0 };

            if (text.Length == 0)
                return boundaries;

            int position = 0;
            Script runScript = Script.Common;

            foreach (Rune rune in text.EnumerateRunes())
            {
                Script script = getScript(rune);

                if (script != Script.Common)
                {
                    if (runScript == Script.Common)
                        runScript = script;
                    else if (script != runScript)
                    {
                        boundaries.Add(position);
                        runScript = script;
                    }
                }

                position += rune.Utf16SequenceLength;
            }

            boundaries.Add(text.Length);
            return boundaries;
        }

        /// <summary>
        /// The script of <paramref name="rune"/>, or <see cref="Script.Common"/> if it has none of its own.
        /// </summary>
        private static Script getScript(Rune rune)
        {
            int codePoint = rune.Value;
            int low = 0;
            int high = ranges.Length - 1;

            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                var range = ranges[middle];

                if (codePoint < range.Start)
                    high = middle - 1;
                else if (codePoint > range.End)
                    low = middle + 1;
                else
                    return range.Script;
            }

            return Script.Common;
        }
    }
}
