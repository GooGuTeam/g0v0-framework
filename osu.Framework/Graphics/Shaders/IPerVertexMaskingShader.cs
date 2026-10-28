// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Graphics.Shaders
{
    /// <summary>
    /// A shader that may read masking parameters per primitive (see <see cref="PerVertexMasking"/>).
    /// </summary>
    internal interface IPerVertexMaskingShader
    {
        /// <summary>
        /// Whether this program reads masking parameters and clips to the scissor per primitive.
        /// </summary>
        bool UsesPerVertexMasking { get; }
    }
}
