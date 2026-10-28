// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace osu.Framework.Imaging
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Rgba32 : IPixel<Rgba32>, IEquatable<Rgba32>
    {
        public byte R, G, B, A;
        public Rgba32(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }
        public Rgba32(float r, float g, float b, float a = 1) : this(new Vector4(r, g, b, a)) { }
        public Rgba32(Vector4 v) : this(convert(v.X), convert(v.Y), convert(v.Z), convert(v.W)) { }
        private static byte convert(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        public Vector4 ToVector4() => new Vector4(R, G, B, A) / 255;
        public Rgba32 ToRgba32() => this;
        public void FromRgba32(Rgba32 value) => this = value;
        public bool Equals(Rgba32 other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object? obj) => obj is Rgba32 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public static bool operator ==(Rgba32 a, Rgba32 b) => a.Equals(b);
        public static bool operator !=(Rgba32 a, Rgba32 b) => !a.Equals(b);
    }
}
