// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.OpenGL.Buffers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Textures;
using osu.Framework.Statistics;
using osu.Framework.Utils;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    public static class RendererExtensions
    {
        /// <summary>
        /// Draws a textured triangle to the screen.
        /// </summary>
        /// <param name="renderer">The renderer to draw the texture with.</param>
        /// <param name="texture">The texture to fill the triangle with.</param>
        /// <param name="vertexTriangle">The triangle to draw.</param>
        /// <param name="textureRect">The texture rectangle.</param>
        /// <param name="drawColour">The vertex colour.</param>
        /// <param name="vertexAction">An action that adds vertices to a <see cref="IVertexBatch{T}"/>.</param>
        /// <param name="inflationPercentage">The percentage amount that <paramref name="textureRect"/> should be inflated.</param>
        /// <param name="textureCoords">The texture coordinates of the triangle's vertices (translated from the corresponding quad's rectangle).</param>
        public static void DrawTriangle(this IRenderer renderer, Texture texture, Triangle vertexTriangle, ColourInfo drawColour, RectangleF? textureRect = null,
                                        Action<TexturedVertex2D>? vertexAction = null, Vector2? inflationPercentage = null, RectangleF? textureCoords = null)
        {
            ObjectDisposedException.ThrowIf(!texture.Available, texture);

            if (!bindOrResolveUntextured(renderer, texture, textureRect, inflationPercentage, textureCoords, false, out RectangleF texRect, out RectangleF inflatedCoordRect, out Vector2 inflationAmount))
                return;

            vertexAction ??= renderer.DefaultQuadBatch.AddAction;

            // We split the triangle into two, such that we can obtain smooth edges with our
            // texture coordinate trick. We might want to revert this to drawing a single
            // triangle in case we ever need proper texturing, or if the additional vertices
            // end up becoming an overhead (unlikely).
            SRGBColour topColour = (drawColour.TopLeft + drawColour.TopRight) / 2;
            SRGBColour bottomColour = (drawColour.BottomLeft + drawColour.BottomRight) / 2;

            vertexAction(new TexturedVertex2D(renderer)
            {
                Position = vertexTriangle.P0,
                TexturePosition = new Vector2((inflatedCoordRect.Left + inflatedCoordRect.Right) / 2, inflatedCoordRect.Top),
                TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                BlendRange = inflationAmount,
                Colour = topColour.SRGB,
            });
            vertexAction(new TexturedVertex2D(renderer)
            {
                Position = vertexTriangle.P1,
                TexturePosition = new Vector2(inflatedCoordRect.Left, inflatedCoordRect.Bottom),
                TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                BlendRange = inflationAmount,
                Colour = drawColour.BottomLeft.SRGB,
            });
            vertexAction(new TexturedVertex2D(renderer)
            {
                Position = (vertexTriangle.P1 + vertexTriangle.P2) / 2,
                TexturePosition = new Vector2((inflatedCoordRect.Left + inflatedCoordRect.Right) / 2, inflatedCoordRect.Bottom),
                TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                BlendRange = inflationAmount,
                Colour = bottomColour.SRGB,
            });
            vertexAction(new TexturedVertex2D(renderer)
            {
                Position = vertexTriangle.P2,
                TexturePosition = new Vector2(inflatedCoordRect.Right, inflatedCoordRect.Bottom),
                TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                BlendRange = inflationAmount,
                Colour = drawColour.BottomRight.SRGB,
            });

            long area = (long)vertexTriangle.Area;

            // Area may incorrect return a negative value, so guard against that case for now.
            // See https://sentry.ppy.sh/organizations/ppy/issues/5739.
            if (area > 0)
                FrameStatistics.Add(StatisticsCounterType.Pixels, area);
        }

        /// <summary>
        /// Draws a textured quad to the screen.
        /// </summary>
        /// <param name="renderer">The renderer to draw the texture with.</param>
        /// <param name="texture">The texture to fill the triangle with.</param>
        /// <param name="vertexQuad">The quad to draw.</param>
        /// <param name="textureRect">The texture rectangle.</param>
        /// <param name="drawColour">The vertex colour.</param>
        /// <param name="vertexAction">An action that adds vertices to a <see cref="IVertexBatch{T}"/>.</param>
        /// <param name="inflationPercentage">The percentage amount that <paramref name="textureRect"/> should be inflated.</param>
        /// <param name="blendRangeOverride">The range over which the edges of the <paramref name="textureRect"/> should be blended.</param>
        /// <param name="textureCoords">The texture coordinates of the quad's vertices.</param>
        public static void DrawQuad(this IRenderer renderer, Texture texture, Quad vertexQuad, ColourInfo drawColour, RectangleF? textureRect = null, Action<TexturedVertex2D>? vertexAction = null,
                                    Vector2? inflationPercentage = null, Vector2? blendRangeOverride = null, RectangleF? textureCoords = null)
        {
            ObjectDisposedException.ThrowIf(!texture.Available, texture);

            if (!bindOrResolveUntextured(renderer, texture, textureRect, inflationPercentage, textureCoords, blendRangeOverride.HasValue, out RectangleF texRect, out RectangleF inflatedCoordRect,
                    out Vector2 inflationAmount))
                return;

            Vector2 blendRange = blendRangeOverride ?? inflationAmount;

            // Explicit callbacks retain their individual calls, including any state changes between vertices.
            if (vertexAction != null || !tryAddQuad(renderer, vertexQuad, drawColour, texRect, inflatedCoordRect, blendRange))
            {
                vertexAction ??= renderer.DefaultQuadBatch.AddAction;

                vertexAction(new TexturedVertex2D(renderer)
                {
                    Position = vertexQuad.BottomLeft,
                    TexturePosition = new Vector2(inflatedCoordRect.Left, inflatedCoordRect.Bottom),
                    TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                    BlendRange = blendRange,
                    Colour = drawColour.BottomLeft.SRGB,
                });
                vertexAction(new TexturedVertex2D(renderer)
                {
                    Position = vertexQuad.BottomRight,
                    TexturePosition = new Vector2(inflatedCoordRect.Right, inflatedCoordRect.Bottom),
                    TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                    BlendRange = blendRange,
                    Colour = drawColour.BottomRight.SRGB,
                });
                vertexAction(new TexturedVertex2D(renderer)
                {
                    Position = vertexQuad.TopRight,
                    TexturePosition = new Vector2(inflatedCoordRect.Right, inflatedCoordRect.Top),
                    TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                    BlendRange = blendRange,
                    Colour = drawColour.TopRight.SRGB,
                });
                vertexAction(new TexturedVertex2D(renderer)
                {
                    Position = vertexQuad.TopLeft,
                    TexturePosition = new Vector2(inflatedCoordRect.Left, inflatedCoordRect.Top),
                    TextureRect = new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                    BlendRange = blendRange,
                    Colour = drawColour.TopLeft.SRGB,
                });
            }

            long area = (long)vertexQuad.Area;

            // Area may incorrect return a negative value, so guard against that case for now.
            // See https://sentry.ppy.sh/organizations/ppy/issues/5739.
            if (area > 0)
                FrameStatistics.Add(StatisticsCounterType.Pixels, area);
        }

        /// <summary>
        /// Binds <paramref name="texture"/> and resolves its coordinates, or resolves coordinates for an untextured draw
        /// when <see cref="Renderer.CanDrawUntextured"/> allows the texture to remain unbound.
        /// </summary>
        /// <returns>Whether the texture can be drawn.</returns>
        private static bool bindOrResolveUntextured(IRenderer renderer, Texture texture, RectangleF? textureRect, Vector2? inflationPercentage, RectangleF? textureCoords, bool hasBlendRangeOverride,
                                                    out RectangleF texRect, out RectangleF inflatedCoordRect, out Vector2 inflationAmount)
        {
            // An explicit blend range is not expressed in texture space, so only accept it where the drawing-rect blend
            // cannot apply (coordinates equal to the texture rectangle). Otherwise its scale relative to the coordinates would change.
            bool blendCompatible = !hasBlendRangeOverride || (textureCoords == null && (inflationPercentage ?? Vector2.Zero) == Vector2.Zero);

            if (blendCompatible && renderer is Renderer concrete && concrete.CanDrawUntextured(texture))
            {
                RectangleF whiteRect = texture.GetTextureRect(textureRect);

                if (whiteRect.Width > 0 && whiteRect.Height > 0)
                {
                    // Map the white pixel's texture space affinely onto the marker rectangle, preserving every relation the
                    // shader uses between coordinates, rectangle and blend range.
                    RectangleF untextured = Renderer.UNTEXTURED_RECT;
                    Vector2 scale = new Vector2(untextured.Width / whiteRect.Width, untextured.Height / whiteRect.Height);
                    RectangleF coordRect = textureCoords.HasValue ? texture.GetTextureRect(textureCoords) : whiteRect;

                    coordRect = new RectangleF(
                        untextured.X + (coordRect.X - whiteRect.X) * scale.X,
                        untextured.Y + (coordRect.Y - whiteRect.Y) * scale.Y,
                        coordRect.Width * scale.X,
                        coordRect.Height * scale.Y);

                    texRect = untextured;
                    inflationAmount = inflationPercentage.HasValue ? new Vector2(inflationPercentage.Value.X * untextured.Width, inflationPercentage.Value.Y * untextured.Height) : Vector2.Zero;
                    inflatedCoordRect = coordRect.Inflate(inflationAmount);
                    return true;
                }
            }

            if (!renderer.BindTexture(texture))
            {
                texRect = inflatedCoordRect = default;
                inflationAmount = default;
                return false;
            }

            GetQuadTextureCoordinates(renderer, texture, textureRect, inflationPercentage, textureCoords, out texRect, out inflatedCoordRect, out inflationAmount);
            return true;
        }

        /// <summary>
        /// Resolves quad sampling coordinates after binding the texture, including atlas wrapping and edge inflation.
        /// </summary>
        internal static void GetQuadTextureCoordinates(IRenderer renderer, Texture texture, RectangleF? textureRect, Vector2? inflationPercentage, RectangleF? textureCoords,
                                                       out RectangleF texRect, out RectangleF inflatedCoordRect, out Vector2 inflationAmount)
        {
            texRect = texture.GetTextureRect(textureRect);
            RectangleF coordRect = textureCoords.HasValue ? texture.GetTextureRect(textureCoords) : texRect;
            inflationAmount = inflationPercentage.HasValue ? new Vector2(inflationPercentage.Value.X * texRect.Width, inflationPercentage.Value.Y * texRect.Height) : Vector2.Zero;

            // Capture coordinates before expanding the clamp bounds, just as in ordinary quad drawing.
            if (renderer.CurrentWrapModeS == WrapMode.ClampToEdge || renderer.CurrentWrapModeT == WrapMode.ClampToEdge)
            {
                Vector2 inflationVector = Vector2.Zero;

                const int mipmap_padding_requirement = (1 << IRenderer.MAX_MIPMAP_LEVELS) / 2;

                if (renderer.CurrentWrapModeS == WrapMode.ClampToEdge)
                    inflationVector.X = mipmap_padding_requirement / (float)texture.Width;
                if (renderer.CurrentWrapModeT == WrapMode.ClampToEdge)
                    inflationVector.Y = mipmap_padding_requirement / (float)texture.Height;
                texRect = texRect.Inflate(inflationVector);
            }

            inflatedCoordRect = coordRect.Inflate(inflationAmount);
        }

        private static bool tryAddQuad(IRenderer renderer, in Quad quad, in ColourInfo colour, in RectangleF textureRect, in RectangleF textureCoords, Vector2 blendRange)
        {
            if (renderer.DefaultQuadBatch is not IQuadBatch<TexturedVertex2D> batch)
                return false;

            Span<TexturedVertex2D> vertices = stackalloc TexturedVertex2D[IRenderer.VERTICES_PER_QUAD];
            var vertex = new TexturedVertex2D(renderer)
            {
                TextureRect = new Vector4(textureRect.Left, textureRect.Top, textureRect.Right, textureRect.Bottom),
                BlendRange = blendRange,
            };

            vertex.Position = quad.BottomLeft;
            vertex.TexturePosition = new Vector2(textureCoords.Left, textureCoords.Bottom);
            vertex.Colour = colour.BottomLeft.SRGB;
            vertices[0] = vertex;

            vertex.Position = quad.BottomRight;
            vertex.TexturePosition = new Vector2(textureCoords.Right, textureCoords.Bottom);
            vertex.Colour = colour.BottomRight.SRGB;
            vertices[1] = vertex;

            vertex.Position = quad.TopRight;
            vertex.TexturePosition = new Vector2(textureCoords.Right, textureCoords.Top);
            vertex.Colour = colour.TopRight.SRGB;
            vertices[2] = vertex;

            vertex.Position = quad.TopLeft;
            vertex.TexturePosition = new Vector2(textureCoords.Left, textureCoords.Top);
            vertex.Colour = colour.TopLeft.SRGB;
            vertices[3] = vertex;

            return TryAddQuadRetainingMasking(renderer, batch, vertices, quad, blendRange);
        }

        /// <summary>
        /// Submits a complete quad, keeping the applied clipping state when the quad renders identically under it.
        /// </summary>
        internal static bool TryAddQuadRetainingMasking(IRenderer renderer, IQuadBatch<TexturedVertex2D> batch, ReadOnlySpan<TexturedVertex2D> vertices, in Quad quad, Vector2 blendRange)
        {
            if (renderer is not Renderer concrete || !concrete.TryRetainAppliedMasking(batch, quad, blendRange))
                return batch.TryAddQuad(vertices);

            try
            {
                return batch.TryAddQuad(vertices);
            }
            finally
            {
                concrete.EndRetainAppliedMasking();
            }
        }

        /// <summary>
        /// Clips a <see cref="IConvexPolygon"/> to the current masking area and draws the resulting triangles to the screen using the specified texture.
        /// </summary>
        /// <param name="renderer">The renderer to draw the texture with.</param>
        /// <param name="polygon">The polygon to draw.</param>
        /// <param name="texture">The texture to fill the triangle with.</param>
        /// <param name="textureRect">The texture rectangle.</param>
        /// <param name="drawColour">The vertex colour.</param>
        /// <param name="vertexAction">An action that adds vertices to a <see cref="IVertexBatch{T}"/>.</param>
        /// <param name="inflationPercentage">The percentage amount that <paramref name="textureRect"/> should be inflated.</param>
        /// <param name="textureCoords">The texture coordinates of the polygon's vertices (translated from the corresponding quad's rectangle).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DrawClipped<T>(this IRenderer renderer, ref T polygon, Texture texture, ColourInfo drawColour, RectangleF? textureRect = null, Action<TexturedVertex2D>? vertexAction = null,
                                          Vector2? inflationPercentage = null, RectangleF? textureCoords = null)
            where T : IConvexPolygon
        {
            var maskingQuad = renderer.CurrentMaskingInfo.ConservativeScreenSpaceQuad;

            var clipper = new ConvexPolygonClipper<Quad, T>(ref maskingQuad, ref polygon);
            Span<Vector2> buffer = stackalloc Vector2[clipper.GetClipBufferSize()];
            Span<Vector2> clippedRegion = clipper.Clip(buffer);

            for (int i = 2; i < clippedRegion.Length; i++)
                renderer.DrawTriangle(texture, new Triangle(clippedRegion[0], clippedRegion[i - 1], clippedRegion[i]), drawColour, textureRect, vertexAction, inflationPercentage, textureCoords);
        }

        /// <summary>
        /// Draws a <see cref="GLFrameBuffer"/> to the screen.
        /// </summary>
        /// <param name="renderer">The renderer to draw the framebuffer with.</param>
        /// <param name="frameBuffer">The <see cref="GLFrameBuffer"/> to draw.</param>
        /// <param name="vertexQuad">The destination vertices.</param>
        /// <param name="drawColour">The colour to draw the <paramref name="frameBuffer"/> with.</param>
        /// <param name="vertexAction">An action that adds vertices to a <see cref="IVertexBatch{T}"/>.</param>
        /// <param name="inflationPercentage">The percentage amount that the frame buffer area  should be inflated.</param>
        /// <param name="blendRangeOverride">The range over which the edges of the frame buffer should be blended.</param>
        public static void DrawFrameBuffer(this IRenderer renderer, IFrameBuffer frameBuffer, Quad vertexQuad, ColourInfo drawColour, Action<TexturedVertex2D>? vertexAction = null,
                                           Vector2? inflationPercentage = null, Vector2? blendRangeOverride = null)
        {
            renderer.DrawQuad(frameBuffer.Texture, vertexQuad, drawColour, null, vertexAction, inflationPercentage, blendRangeOverride);
        }

        /// <summary>
        /// Applies a new orthographic projection rectangle.
        /// </summary>
        /// <remarks>
        /// After usage, restore the last state with <see cref="PopOrtho"/> or <see cref="IRenderer.PopProjectionMatrix"/>.
        /// </remarks>
        /// <param name="renderer">The renderer.</param>
        /// <param name="ortho">The rectangle to create the orthographic projection from.</param>
        public static void PushOrtho(this IRenderer renderer, RectangleF ortho)
        {
            renderer.PushProjectionMatrix(Matrix4.CreateOrthographicOffCenter(ortho.Left, ortho.Right, ortho.Bottom, ortho.Top, -1, 1));
        }

        /// <summary>
        /// Restores the last projection rectangle.
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        public static void PopOrtho(this IRenderer renderer) => renderer.PopProjectionMatrix();

        /// <summary>
        /// Applies a new projection matrix so that all drawn vertices are transformed by <paramref name="matrix"/>. This also affects masking.
        /// </summary>
        /// <remarks>
        /// After usage, restore the last state with <see cref="PopLocalMatrix"/>.
        /// </remarks>
        /// <param name="renderer">The renderer.</param>
        /// <param name="matrix">The matrix.</param>
        public static void PushLocalMatrix(this IRenderer renderer, Matrix4 matrix)
        {
            var currentMasking = renderer.CurrentMaskingInfo;
            // normally toMaskingSpace is fed vertices already in screen space coordinates,
            // but since we are modifying the matrix the vertices are in local space
            currentMasking.ToMaskingSpace = new Matrix3(matrix) * currentMasking.ToMaskingSpace;
            renderer.PushMaskingInfo(currentMasking, true);
            renderer.PushProjectionMatrix(matrix * renderer.ProjectionMatrix);
        }

        /// <inheritdoc cref="PushLocalMatrix(IRenderer, Matrix4)"/>
        public static void PushLocalMatrix(this IRenderer renderer, Matrix3 matrix)
        {
            var currentMasking = renderer.CurrentMaskingInfo;
            // normally toMaskingSpace is fed vertices already in screen space coordinates,
            // but since we are modifying the matrix the vertices are in local space
            currentMasking.ToMaskingSpace = matrix * currentMasking.ToMaskingSpace;
            renderer.PushMaskingInfo(currentMasking, true);

            // this makes sure it also works for 3D vertices like the ones path uses
            Matrix4 mat = new Matrix4(matrix);
            mat.Row3.X = mat.Row2.X;
            mat.Row2.X = 0;
            mat.Row3.Y = mat.Row2.Y;
            mat.Row2.Y = 0;
            renderer.PushProjectionMatrix(mat * renderer.ProjectionMatrix);
        }

        /// <summary>
        /// Restores the last projection matrix after a call to <see cref="PushLocalMatrix(IRenderer, Matrix4)"/>.
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        public static void PopLocalMatrix(this IRenderer renderer)
        {
            renderer.PopProjectionMatrix();
            renderer.PopMaskingInfo();
        }
    }
}
