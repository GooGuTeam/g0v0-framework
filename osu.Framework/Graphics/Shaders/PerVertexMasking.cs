// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text.RegularExpressions;

namespace osu.Framework.Graphics.Shaders
{
    /// <summary>
    /// Decides whether a shader program reads masking parameters per primitive from the masking buffer (see <c>sh_Masking.h</c>),
    /// so that masking changes need not split batches.
    /// </summary>
    /// <remarks>
    /// A program opts in when its vertex shader declares the <c>m_MaskingIndex</c> input (as <c>sh_Texture2D.vs</c> does) and its
    /// fragment shader calls <c>getRoundedColor()</c>. Such programs also clip to the scissor rectangle in the fragment shader, which
    /// lets the renderer leave the hardware scissor at the whole viewport. Fragment shaders that include <c>sh_Masking.h</c> but
    /// write colours without <c>getRoundedColor()</c> would not be clipped, so they keep the global-uniform path, as do programs
    /// with other vertex shaders.
    /// </remarks>
    internal static class PerVertexMasking
    {
        public const string DEFINE = "G0V0_PER_VERTEX_MASKING";

        /// <summary>
        /// Fragment input locations of the per-vertex masking varyings declared by <c>sh_Masking.h</c>.
        /// </summary>
        public static readonly int[] FRAGMENT_INPUT_LOCATIONS = { 5, 6 };

        /// <summary>
        /// Whether programs may opt in. Initialised from the framework settings before shaders are compiled.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        private static readonly Regex vertex_input = new Regex(@"^\s*layout\s*\(\s*location\s*=\s*\d+\s*\)\s*in\s+(?:\w+\s+)?float\s+m_MaskingIndex\s*;", RegexOptions.Multiline);
        private static readonly Regex rounded_colour_call = new Regex(@"\bgetRoundedColor\s*\(");
        private static readonly Regex rounded_colour_definition = new Regex(@"\bvec4\s+getRoundedColor\s*\(");

        public static bool IsSupported(string vertexText, string fragmentText)
            => Enabled
               && vertex_input.IsMatch(vertexText)
               && rounded_colour_call.Count(fragmentText) > rounded_colour_definition.Count(fragmentText);

        /// <summary>
        /// Adds the opt-in definition to a shader's text, after its <c>#version</c> directive.
        /// </summary>
        public static string AddDefine(string text) => AddDefine(text, DEFINE);

        /// <summary>
        /// Adds a definition to a shader's text, after its <c>#version</c> directive.
        /// </summary>
        public static string AddDefine(string text, string define)
        {
            int version = text.IndexOf("#version", StringComparison.Ordinal);
            int insertAt = version < 0 ? 0 : text.IndexOf('\n', version) + 1;
            return text.Insert(insertAt, $"#define {define}\n");
        }
    }
}
