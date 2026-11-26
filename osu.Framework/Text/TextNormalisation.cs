// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using osu.Framework.Logging;

namespace osu.Framework.Text
{
    /// <summary>
    /// Unicode normalisation helpers for text which is about to be shaped or measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Text reaching the framework does not have a single canonical encoding: input methods on macOS emit decomposed
    /// sequences, while most Windows and server-side sources emit precomposed characters. Both render identically in a
    /// shaping renderer, but they are different strings, which matters wherever text is compared, searched or hashed.
    /// </para>
    /// <para>
    /// Normalisation requires ICU (or an equivalent implementation of <see cref="string.Normalize(NormalizationForm)"/>).
    /// Under <c>InvariantGlobalization</c> the BCL does <b>not</b> throw: it silently returns the input unchanged and
    /// reports <see cref="string.IsNormalized(NormalizationForm)"/> as <c>true</c> for any input. Support is therefore
    /// probed by observing an actual transformation rather than by catching an exception, and callers are told about the
    /// degradation once via <see cref="IsSupported"/>.
    /// </para>
    /// </remarks>
    public static class TextNormalisation
    {
        /// <summary>
        /// A decomposed <c>é</c> (U+0065 U+0301), which must compose to a single code point under form C.
        /// </summary>
        private const string probe_text = "e\u0301";

        private static readonly Lazy<bool> supported = new Lazy<bool>(probeSupport);

        /// <summary>
        /// Whether Unicode normalisation is available in the current runtime.
        /// </summary>
        /// <remarks>
        /// When this is <c>false</c>, <see cref="Normalise"/> returns its input unchanged.
        /// </remarks>
        public static bool IsSupported => supported.Value;

        /// <summary>
        /// Returns <paramref name="text"/> in Unicode normalisation form C (canonical composition).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Form C is used rather than form KC because compatibility decomposition would rewrite characters which are
        /// deliberately distinct, such as full-width punctuation and the ligature characters themselves.
        /// </para>
        /// <para>
        /// Text containing an unpaired surrogate cannot be normalised by the BCL at all and is returned unchanged.
        /// </para>
        /// </remarks>
        /// <param name="text">The text to normalise.</param>
        /// <returns>The normalised text, or <paramref name="text"/> itself if it is already normalised or unsupported.</returns>
        public static string Normalise(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (!IsSupported)
                return text;

            // ASCII can never change under any normalisation form, and it is by far the most common case.
            if (isAscii(text))
                return text;

            try
            {
                return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                // Text containing an unpaired surrogate - usually a string which was cut in the middle of a surrogate
                // pair - cannot be normalised at all, not even inspected. Drawing it as-is is strictly better than
                // refusing to draw the surrounding text.
                return text;
            }
        }

        private static bool isAscii(string text)
        {
            foreach (char c in text)
            {
                if (c >= '\u0080')
                    return false;
            }

            return true;
        }

        private static bool probeSupport()
        {
            bool available;

            try
            {
                available = probe_text.Normalize(NormalizationForm.FormC).Length == 1;
            }
            catch (Exception)
            {
                // Not expected - the failure mode under invariant globalization is a silent no-op rather than a throw -
                // but a throwing implementation must not take down rendering.
                available = false;
            }

            if (!available)
            {
                Logger.Log(
                    $"Unicode normalisation is unavailable ({nameof(NormalizationForm.FormC)} text will be rendered as-is; "
                    + "text may differ from server-side data for names containing combining marks). "
                    + "This is expected under InvariantGlobalization.",
                    LoggingTarget.Runtime,
                    LogLevel.Important);
            }

            return available;
        }
    }
}
