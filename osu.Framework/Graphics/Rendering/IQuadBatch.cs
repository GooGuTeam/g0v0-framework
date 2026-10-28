// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Rendering.Vertices;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Internal fast path for submitting four ordered vertices without changing the public vertex-batch contract.
    /// </summary>
    internal interface IQuadBatch<TVertex>
        where TVertex : unmanaged, IEquatable<TVertex>, IVertex
    {
        /// <summary>
        /// Submits exactly four vertices. Returns false without changing batch state if individual vertex submission is required.
        /// The input is consumed synchronously and must not be retained.
        /// </summary>
        bool TryAddQuad(ReadOnlySpan<TVertex> vertices);
    }
}
