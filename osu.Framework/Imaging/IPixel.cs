// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
namespace osu.Framework.Imaging
{
    public interface IPixel<T> where T : unmanaged, IPixel<T>
    {
        Rgba32 ToRgba32();
        void FromRgba32(Rgba32 value);
    }
}
