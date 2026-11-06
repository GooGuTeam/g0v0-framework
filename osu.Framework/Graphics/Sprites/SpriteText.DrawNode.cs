// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Textures;
using osu.Framework.Statistics;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Graphics.Sprites
{
    public partial class SpriteText
    {
        internal class SpriteTextDrawNode : TexturedShaderDrawNode
        {
            protected new SpriteText Source => (SpriteText)base.Source;

            private bool shadow;
            private ColourInfo finalShadowColour;
            private ColourInfo shadowSourceColour;
            private Color4 shadowColour;
            private Vector2 shadowOffset;

            private List<ScreenSpaceCharacterPart>? parts;
            private long charactersVersion;
            private Matrix3 characterMatrix;
            private Vector2 characterInflation;

            public SpriteTextDrawNode(SpriteText source)
                : base(source)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();

                bool geometryChanged = updateScreenSpaceCharacters();
                bool wasShadowEnabled = shadow;
                shadow = Source.Shadow;

                if (shadow)
                {
                    Vector2 offset = Source.premultipliedShadowOffset;

                    if (geometryChanged || !wasShadowEnabled || shadowOffset != offset)
                    {
                        foreach (ref var part in CollectionsMarshal.AsSpan(parts))
                        {
                            part.ShadowQuad = new Quad(
                                part.DrawQuad.TopLeft + offset,
                                part.DrawQuad.TopRight + offset,
                                part.DrawQuad.BottomLeft + offset,
                                part.DrawQuad.BottomRight + offset);
                            part.ShadowPixelArea = Math.Max(0, (long)part.ShadowQuad.Area);
                        }
                    }

                    shadowOffset = offset;

                    ColourInfo sourceColour = DrawColourInfo.Colour;
                    Color4 currentShadowColour = Source.ShadowColour;

                    if (!wasShadowEnabled || !shadowSourceColour.Equals(sourceColour) || shadowColour != currentShadowColour)
                    {
                        var avgColour = (Color4)sourceColour.AverageColour;
                        float shadowAlpha = MathF.Pow(Math.Max(Math.Max(avgColour.R, avgColour.G), avgColour.B), 2);

                        // Position changes do not affect the quadratic fade or colour-space composition.
                        finalShadowColour = sourceColour;
                        finalShadowColour.ApplyChild(((ColourInfo)currentShadowColour).MultiplyAlpha(shadowAlpha));
                        shadowSourceColour = sourceColour;
                        shadowColour = currentShadowColour;
                    }
                }
            }

            protected override void Draw(IRenderer renderer)
            {
                Debug.Assert(parts != null);

                base.Draw(renderer);

                BindTextureShader(renderer);

                // Reuse the colour/depth fields and stack storage across glyphs, without retaining GPU resources.
                Span<TexturedVertex2D> vertices = stackalloc TexturedVertex2D[IRenderer.VERTICES_PER_QUAD];
                Span<TexturedVertex2D> shadowVertices = stackalloc TexturedVertex2D[IRenderer.VERTICES_PER_QUAD];
                ColourInfo drawColour = DrawColourInfo.Colour;
                initialiseVertices(renderer, vertices, drawColour);

                if (shadow)
                    initialiseVertices(renderer, shadowVertices, finalShadowColour);

                foreach (ref readonly var part in CollectionsMarshal.AsSpan(parts))
                {
                    if (renderer.DefaultQuadBatch is not IQuadBatch<TexturedVertex2D> batch)
                    {
                        if (shadow)
                            renderer.DrawQuad(part.Texture, part.ShadowQuad, finalShadowColour, inflationPercentage: part.InflationPercentage);

                        renderer.DrawQuad(part.Texture, part.DrawQuad, drawColour, inflationPercentage: part.InflationPercentage);
                        continue;
                    }

                    // Each glyph still checks uploads and wrapping. Its shadow and foreground share this binding.
                    ObjectDisposedException.ThrowIf(!part.Texture.Available, part.Texture);

                    if (!renderer.BindTexture(part.Texture))
                    {
                        // Preserve the foreground's retry if the shadow encountered an uninitialised texture.
                        if (shadow)
                            renderer.DrawQuad(part.Texture, part.DrawQuad, drawColour, inflationPercentage: part.InflationPercentage);

                        continue;
                    }

                    RendererExtensions.GetQuadTextureCoordinates(renderer, part.Texture, null, part.InflationPercentage, null,
                        out RectangleF textureRect, out RectangleF textureCoords, out Vector2 blendRange);

                    Vector4 textureBounds = new Vector4(textureRect.Left, textureRect.Top, textureRect.Right, textureRect.Bottom);

                    if (shadow)
                    {
                        prepareVertices(shadowVertices, part.ShadowQuad, textureBounds, textureCoords, blendRange);
                        drawGlyphQuad(renderer, batch, shadowVertices, part.Texture, part.ShadowQuad, finalShadowColour, part.InflationPercentage, part.ShadowPixelArea);
                    }

                    prepareVertices(vertices, part.DrawQuad, textureBounds, textureCoords, blendRange);
                    drawGlyphQuad(renderer, batch, vertices, part.Texture, part.DrawQuad, drawColour, part.InflationPercentage, part.PixelArea);
                }

                UnbindTextureShader(renderer);
            }

            private static void initialiseVertices(IRenderer renderer, Span<TexturedVertex2D> vertices, in ColourInfo colour)
            {
                var vertex = new TexturedVertex2D(renderer) { Colour = colour.BottomLeft.SRGB };
                vertices[0] = vertex;
                vertex.Colour = colour.BottomRight.SRGB;
                vertices[1] = vertex;
                vertex.Colour = colour.TopRight.SRGB;
                vertices[2] = vertex;
                vertex.Colour = colour.TopLeft.SRGB;
                vertices[3] = vertex;
            }

            private static void prepareVertices(Span<TexturedVertex2D> vertices, in Quad quad, Vector4 textureBounds, in RectangleF textureCoords, Vector2 blendRange)
            {
                vertices[0].Position = quad.BottomLeft;
                vertices[1].Position = quad.BottomRight;
                vertices[2].Position = quad.TopRight;
                vertices[3].Position = quad.TopLeft;

                vertices[0].TexturePosition = new Vector2(textureCoords.Left, textureCoords.Bottom);
                vertices[1].TexturePosition = new Vector2(textureCoords.Right, textureCoords.Bottom);
                vertices[2].TexturePosition = new Vector2(textureCoords.Right, textureCoords.Top);
                vertices[3].TexturePosition = new Vector2(textureCoords.Left, textureCoords.Top);

                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i].TextureRect = textureBounds;
                    vertices[i].BlendRange = blendRange;
                }
            }

            private static void drawGlyphQuad(IRenderer renderer, IQuadBatch<TexturedVertex2D> batch, ReadOnlySpan<TexturedVertex2D> vertices,
                                              Texture texture, in Quad quad, in ColourInfo colour, Vector2 inflationPercentage, long pixelArea)
            {
                if (!RendererExtensions.TryAddQuadRetainingMasking(renderer, batch, vertices, quad, vertices[0].BlendRange))
                {
                    // Retain the existing behaviour if a caller has left a partial primitive in the batch.
                    renderer.DrawQuad(texture, quad, colour, inflationPercentage: inflationPercentage);
                    return;
                }

                if (pixelArea > 0)
                    FrameStatistics.Add(StatisticsCounterType.Pixels, pixelArea);
            }

            /// <summary>
            /// Updates screen-space geometry and reports whether the cached quads changed.
            /// </summary>
            private bool updateScreenSpaceCharacters()
            {
                // Resolve the lazy layout before comparing versions; it can also change the drawable's size.
                var characters = Source.characters;
                Matrix3 matrix = Source.DrawInfo.Matrix;
                Vector2 inflationAmount = DrawInfo.MatrixInverse.ExtractScale().Xy;

                if (parts != null && charactersVersion == Source.charactersVersion && characterMatrix.Equals(matrix) && characterInflation == inflationAmount)
                    return false;

                Vector2 translation = matrix.Row2.Xy;
                bool canReuseLinearGeometry = parts != null
                                              && charactersVersion == Source.charactersVersion
                                              && characterInflation == inflationAmount
                                              && characterMatrix.Row0.X == matrix.Row0.X
                                              && characterMatrix.Row0.Y == matrix.Row0.Y
                                              && characterMatrix.Row1.X == matrix.Row1.X
                                              && characterMatrix.Row1.Y == matrix.Row1.Y;

                if (!canReuseLinearGeometry)
                {
                    int partCount = characters.Count;

                    if (parts == null)
                        parts = new List<ScreenSpaceCharacterPart>(partCount);
                    else
                    {
                        parts.Clear();
                        parts.EnsureCapacity(partCount);
                    }

                    foreach (var character in characters)
                    {
                        Quad localQuad = Quad.FromRectangle(character.DrawRectangle.Inflate(inflationAmount));

                        var part = new ScreenSpaceCharacterPart
                        {
                            LinearQuad = new Quad(
                                transformWithoutTranslation(localQuad.TopLeft, matrix),
                                transformWithoutTranslation(localQuad.TopRight, matrix),
                                transformWithoutTranslation(localQuad.BottomLeft, matrix),
                                transformWithoutTranslation(localQuad.BottomRight, matrix)),
                            InflationPercentage = new Vector2(
                                character.DrawRectangle.Size.X == 0 ? 0 : inflationAmount.X / character.DrawRectangle.Size.X,
                                character.DrawRectangle.Size.Y == 0 ? 0 : inflationAmount.Y / character.DrawRectangle.Size.Y),
                            Texture = character.Texture
                        };

                        applyTranslation(ref part, translation);
                        parts.Add(part);
                    }
                }
                else
                {
                    Span<ScreenSpaceCharacterPart> currentParts = CollectionsMarshal.AsSpan(parts);

                    for (int i = 0; i < currentParts.Length; i++)
                    {
                        ref var part = ref currentParts[i];
                        // Preserve texture refresh on transform changes, including custom glyph implementations.
                        part.Texture = characters[i].Texture;
                        applyTranslation(ref part, translation);
                    }
                }

                charactersVersion = Source.charactersVersion;
                characterMatrix = matrix;
                characterInflation = inflationAmount;
                return true;
            }

            private static void applyTranslation(ref ScreenSpaceCharacterPart part, Vector2 translation)
            {
                // Add the absolute translation to the cached linear result, never to a previously translated quad.
                part.DrawQuad = new Quad(
                    part.LinearQuad.TopLeft + translation,
                    part.LinearQuad.TopRight + translation,
                    part.LinearQuad.BottomLeft + translation,
                    part.LinearQuad.BottomRight + translation);

                // Translation can change floating-point area rounding, so keep statistics based on the final coordinates.
                part.PixelArea = Math.Max(0, (long)part.DrawQuad.Area);
            }

            // Match Vector2Extensions.Transform's multiply/add order, leaving its final translation for each applied state.
            private static Vector2 transformWithoutTranslation(Vector2 position, in Matrix3 matrix) => new Vector2(
                matrix.Row0.X * position.X + matrix.Row1.X * position.Y,
                matrix.Row0.Y * position.X + matrix.Row1.Y * position.Y);
        }

        /// <summary>
        /// A character of a <see cref="SpriteText"/> provided with screen space draw coordinates.
        /// </summary>
        internal struct ScreenSpaceCharacterPart
        {
            /// <summary>
            /// The inflated quad after scaling, rotation and shear, before the final screen-space translation.
            /// </summary>
            public Quad LinearQuad;

            /// <summary>
            /// The screen-space quad for the character to be drawn in.
            /// </summary>
            public Quad DrawQuad;

            /// <summary>
            /// Shadow geometry, rebuilt only when the glyph geometry or shadow offset changes.
            /// </summary>
            public Quad ShadowQuad;

            /// <summary>
            /// Submission areas for statistics, including the shadow's independently rounded coordinates.
            /// </summary>
            public long PixelArea;
            public long ShadowPixelArea;

            /// <summary>
            /// Extra padding for the character's texture.
            /// </summary>
            public Vector2 InflationPercentage;

            /// <summary>
            /// The texture to draw the character with.
            /// </summary>
            public Texture Texture;
        }
    }
}
