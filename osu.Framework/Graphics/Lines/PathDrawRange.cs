// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK;

namespace osu.Framework.Graphics.Lines
{
    /// <summary>
    /// A visible path consisting of an interpolated start, a contiguous range of original vertices,
    /// and an interpolated end. Endpoints use the same coordinates as <see cref="Path.Vertices"/>.
    /// </summary>
    public readonly record struct PathDrawRange(int FirstVertex, int VertexCount, Vector2 StartPoint, Vector2 EndPoint);
}
