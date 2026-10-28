// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System.Runtime.InteropServices;
namespace osu.Framework.Imaging
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Bgra32 : IPixel<Bgra32>
    {
        public byte B, G, R, A;
        public Rgba32 ToRgba32() => new Rgba32(R, G, B, A);
        public void FromRgba32(Rgba32 value) { R = value.R; G = value.G; B = value.B; A = value.A; }
    }
}
