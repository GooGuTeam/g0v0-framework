// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    public struct MaskingInfo : IEquatable<MaskingInfo>
    {
        public RectangleI ScreenSpaceAABB;
        public RectangleF MaskingRect;

        public Quad ConservativeScreenSpaceQuad;

        /// <summary>
        /// This matrix transforms screen space coordinates to masking space (likely the parent
        /// space of the container doing the masking).
        /// It is used by a shader to determine which pixels to discard.
        /// </summary>
        public Matrix3 ToMaskingSpace;

        public float CornerRadius;
        public float CornerExponent;

        public float BorderThickness;
        public ColourInfo BorderColour;

        public float BlendRange;
        public float AlphaExponent;

        public Vector2 EdgeOffset;

        public bool Hollow;
        public float HollowCornerRadius;

        public readonly bool Equals(MaskingInfo other) => this == other;

        // Screen-space bounds and the conservative quad are CPU culling/scissor metadata.
        // Their differences alone do not require a new shader uniform or vertex batch.
        internal readonly bool HasSameShaderParameters(in MaskingInfo other) =>
            MaskingRect == other.MaskingRect &&
            ToMaskingSpace == other.ToMaskingSpace &&
            CornerRadius == other.CornerRadius &&
            CornerExponent == other.CornerExponent &&
            BorderThickness == other.BorderThickness &&
            BorderColour.Equals(other.BorderColour) &&
            BlendRange == other.BlendRange &&
            AlphaExponent == other.AlphaExponent &&
            EdgeOffset == other.EdgeOffset &&
            Hollow == other.Hollow &&
            HollowCornerRadius == other.HollowCornerRadius;

        public static bool operator ==(in MaskingInfo left, in MaskingInfo right) =>
            left.ScreenSpaceAABB == right.ScreenSpaceAABB &&
            left.MaskingRect == right.MaskingRect &&
            left.ConservativeScreenSpaceQuad.Equals(right.ConservativeScreenSpaceQuad) &&
            left.ToMaskingSpace == right.ToMaskingSpace &&
            left.CornerRadius == right.CornerRadius &&
            left.CornerExponent == right.CornerExponent &&
            left.BorderThickness == right.BorderThickness &&
            left.BorderColour.Equals(right.BorderColour) &&
            left.BlendRange == right.BlendRange &&
            left.AlphaExponent == right.AlphaExponent &&
            left.EdgeOffset == right.EdgeOffset &&
            left.Hollow == right.Hollow &&
            left.HollowCornerRadius == right.HollowCornerRadius;

        public static bool operator !=(in MaskingInfo left, in MaskingInfo right) => !(left == right);

        public override readonly bool Equals(object? obj) => obj is MaskingInfo other && this == other;

        public override readonly int GetHashCode() => 0; // Shouldn't be used; simplifying implementation here.
    }
}
