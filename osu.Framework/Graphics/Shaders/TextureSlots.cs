// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Text.RegularExpressions;

namespace osu.Framework.Graphics.Shaders
{
    /// <summary>
    /// Decides whether <c>sh_Texture.fs</c> declares its extra texture slots (see <c>Renderer.TEXTURE_SLOTS</c>) in a program.
    /// </summary>
    /// <remarks>
    /// The slots occupy resource sets 1 to 3. Vertex shaders other than <c>sh_Texture2D.vs</c> may use those sets for their own
    /// uniform blocks (as the game's cursor trail does), so the slots are only declared when the vertex shader outputs the slot index.
    /// </remarks>
    internal static class TextureSlots
    {
        public const string DEFINE = "G0V0_TEXTURE_SLOTS";

        private static readonly Regex vertex_output = new Regex(@"^\s*layout\s*\(\s*location\s*=\s*\d+\s*\)\s*out\s+(?:\w+\s+)?float\s+v_TextureSlot\s*;", RegexOptions.Multiline);

        public static bool IsSupported(string vertexText) => vertex_output.IsMatch(vertexText);
    }
}
