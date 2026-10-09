// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System.Runtime.CompilerServices;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Text
{
    public sealed class TexturedCharacterGlyph : ITexturedCharacterGlyph
    {
        public Texture Texture { get; }

        public float XOffset => glyph.XOffset * Scale;
        public float YOffset => glyph.YOffset * Scale;
        public float XAdvance => glyph.XAdvance * Scale;
        public float Baseline => glyph.Baseline * Scale;
        public char Character => glyph.Character;
        public float Width => Texture.Width * Scale / textureScale;
        public float Height => Texture.Height * Scale / textureScale;

        /// <summary>
        /// An adjustment factor in scale. This is applied to all other returned metric properties.
        /// </summary>
        public readonly float Scale;

        private readonly CharacterGlyph glyph;
        private readonly float textureScale;

        /// <summary>
        /// Create a new <see cref="TexturedCharacterGlyph"/> instance.
        /// </summary>
        /// <param name="glyph">The glyph.</param>
        /// <param name="texture">The texture.</param>
        /// <param name="scale">A scale factor to apply to exposed glyph metrics.</param>
        /// <param name="textureScale">Texture resolution relative to the original glyph metrics.</param>
        public TexturedCharacterGlyph(CharacterGlyph glyph, Texture texture, float scale = 1, float textureScale = 1)
        {
            this.glyph = glyph;
            this.textureScale = textureScale;
            Scale = scale;
            Texture = texture;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetKerning<T>(T lastGlyph)
            where T : ICharacterGlyph
            => glyph.GetKerning(lastGlyph) * Scale;
    }
}
