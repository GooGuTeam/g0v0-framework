// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Text
{
    /// <summary>
    /// A shaped glyph with a resolved texture, together with the characters it was shaped from.
    /// </summary>
    /// <remarks>
    /// The cluster is kept alongside the glyph rather than on it because it describes the run, not the glyph, and
    /// <see cref="ITexturedCharacterGlyph"/> is public: adding a member to it would break implementations.
    /// </remarks>
    internal readonly struct ShapedCharacterGlyph
    {
        /// <summary>
        /// The glyph, with its metrics in the pixel space <see cref="TextBuilder"/> lays out in.
        /// </summary>
        public readonly ITexturedCharacterGlyph Glyph;

        /// <summary>
        /// The index of the first UTF-16 code unit of the cluster this glyph belongs to.
        /// </summary>
        public readonly int ClusterStart;

        /// <summary>
        /// The number of UTF-16 code units in the cluster this glyph belongs to.
        /// </summary>
        public readonly int ClusterLength;

        public ShapedCharacterGlyph(ITexturedCharacterGlyph glyph, int clusterStart, int clusterLength)
        {
            Glyph = glyph;
            ClusterStart = clusterStart;
            ClusterLength = clusterLength;
        }

        /// <summary>
        /// The index just past the last character of the cluster this glyph belongs to.
        /// </summary>
        public int ClusterEnd => ClusterStart + ClusterLength;
    }
}
