// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Runtime.InteropServices;
using osu.Framework.Graphics.Shaders.Types;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// A masking buffer entry: the masking parameters and scissor rectangle a primitive is drawn with.
    /// Matches <c>g0v0_MaskingData</c> in <c>sh_GlobalUniforms.h</c> (std140).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal record struct MaskingData
    {
        public UniformMatrix3 ToMaskingSpace;
        public UniformVector4 MaskingRect;
        public UniformVector4 ScissorRect;
        public UniformMatrix4 BorderColour;
        public UniformFloat CornerRadius;
        public UniformFloat CornerExponent;
        public UniformFloat BorderThickness;
        public UniformFloat MaskingBlendRange;
        public UniformFloat AlphaExponent;
        public UniformFloat InnerCornerRadius;
        public UniformVector2 EdgeOffset;
        public UniformBool IsMasking;
        public UniformBool DiscardInner;
        private readonly UniformPadding8 pad;
    }
}
