// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Graphics.Lines
{
    public partial class Path
    {
        /// <summary>
        /// Draws the path straight into the current target, without a framebuffer, as one mesh per colour band from the centre outwards.
        /// Each band writes depth and tests it with <see cref="BufferTestFunction.LessThan"/> at a depth unique to this path in the frame,
        /// so each pixel is written once, by the innermost band covering it, and overlapping parts of the path do not blend twice.
        /// Falls back to the framebuffer when the target has no depth attachment, depth is in use, or the colours are unsuitable.
        /// </summary>
        private class PathDirectDrawNode : DrawNode
        {
            private const float precision = 0.01f;

            /// <summary>
            /// The largest colour error of a band's linear interpolation against the gradient, per channel.
            /// </summary>
            private const float fit_tolerance = 1.5f / 255;

            /// <summary>
            /// The largest colour change within a band. Where the path overlaps itself, a pixel takes the colour of whichever segment of
            /// the innermost covering band is drawn first, which may differ from the nearest segment's colour by up to this much.
            /// </summary>
            private const float max_band_variation = 0.125f;

            private const int max_bands = 16;

            /// <summary>
            /// The largest distance in pixels between a rounded edge and the polygon that approximates it.
            /// </summary>
            private const float arc_tolerance = 0.1f;

            private const float depth_step = 1f / 1024;

            private const int min_batch_quads = 64;
            private const int max_batch_quads = 9000;

            protected new Path Source => (Path)base.Source;

            private PathBufferedDrawNode buffered;

            private readonly List<Vector2> points = new List<Vector2>();
            private long pointsVersion = -1;
            private PathDrawRange? drawRange;
            private float radius;
            private ColourInfo colour;
            private IShader? shader;

            private Color4[]? gradient;
            private Color4[]? bandsGradient;
            private Band[]? bands;

            private readonly List<Vector2> line = new List<Vector2>();
            private readonly List<Vector2> directions = new List<Vector2>();
            private readonly List<TexturedVertex2D> meshVertices = new List<TexturedVertex2D>();

            private IVertexBatch<TexturedVertex2D>? batch;
            private int batchQuadCapacity;

            public PathDirectDrawNode(Path source, PathBufferedDrawNode buffered)
                : base(source)
            {
                this.buffered = buffered;
            }

            public override void ApplyState()
            {
                base.ApplyState();
                buffered.ApplyState();

                // Resolve the lazy segment cache, which versions changes to the vertices.
                _ = Source.segments;

                Vector2 offset = Source.vertexBounds.TopLeft;

                if (pointsVersion != Source.segmentsVersion)
                {
                    points.Clear();
                    foreach (var vertex in Source.vertices)
                        points.Add(vertex - offset);
                    pointsVersion = Source.segmentsVersion;
                }

                drawRange = Source.DrawRange is { } range ? range with { StartPoint = range.StartPoint - offset, EndPoint = range.EndPoint - offset } : null;
                radius = Source.PathRadius;
                colour = Source.FrameBufferDrawColour?.Colour ?? ColourInfo.SingleColour(Color4.White);
                shader = Source.directShader;
                gradient = Source.DirectGradient;
            }

            protected override void Draw(IRenderer renderer)
            {
                if (!canDrawDirect(renderer, out float depth))
                {
                    DrawOther(buffered, renderer);
                    return;
                }

                base.Draw(renderer);

                if (line.Count == 0)
                    return;

                Texture white = renderer.WhitePixel;
                RectangleF textureRect = white.GetTextureRect();

                float previousDepth = renderer.BackbufferDepth;
                renderer.BackbufferDepth.Set(depth);

                var vertex = new TexturedVertex2D(renderer)
                {
                    TexturePosition = textureRect.Centre,
                    TextureRect = new Vector4(textureRect.Left, textureRect.Top, textureRect.Right, textureRect.Bottom),
                };

                renderer.BackbufferDepth.Set(previousDepth);

                IShader directShader = shader!;
                directShader.Bind();
                white.Bind();
                renderer.PushDepthInfo(new DepthInfo(true, true, BufferTestFunction.LessThan));

                directions.Clear();
                for (int i = 0; i < line.Count - 1; i++)
                    directions.Add((line[i + 1] - line[i]).Normalized());

                Matrix3 matrix = DrawInfo.Matrix;
                float scale = MathF.Sqrt(MathF.Abs(matrix.M11 * matrix.M22 - matrix.M12 * matrix.M21));
                Color4 tint = colour.TopLeft.SRGB;

                meshVertices.Clear();
                var mesh = new MeshBuilder(meshVertices, vertex, matrix);

                // Every band shares the subdivision of the outermost edge, so that adjacent bands meet at the same vertices without cracks.
                float radiusPixels = radius * scale;
                mesh.MaxArcStep = radiusPixels > arc_tolerance ? 2 * MathF.Acos(1 - arc_tolerance / radiusPixels) : MathF.PI;

                foreach (var band in bands!)
                {
                    mesh.Inner = band.Inner * radius;
                    mesh.Outer = band.Outer * radius;
                    mesh.InnerColour = multiply(band.InnerColour, tint);
                    mesh.OuterColour = multiply(band.OuterColour, tint);

                    drawBand(ref mesh);
                }

                // A batch sized to the mesh draws it in one call; the renderer's default quad batch holds 100 quads per draw.
                int requiredQuads = Math.Min(meshVertices.Count / IRenderer.VERTICES_PER_QUAD, max_batch_quads);

                if (batch == null || batchQuadCapacity < requiredQuads)
                {
                    int capacity = min_batch_quads;
                    while (capacity < requiredQuads)
                        capacity = Math.Min(capacity * 2, max_batch_quads);

                    batch?.Dispose();
                    batch = renderer.CreateQuadBatch<TexturedVertex2D>(capacity, 10);
                    batchQuadCapacity = Math.Max(capacity, batch.Size / IRenderer.VERTICES_PER_QUAD);
                }

                foreach (var meshVertex in CollectionsMarshal.AsSpan(meshVertices))
                    batch.Add(meshVertex);

                renderer.PopDepthInfo();
                directShader.Unbind();
            }

            private bool canDrawDirect(IRenderer renderer, out float depth)
            {
                depth = 0;

                if (shader == null || gradient == null || radius <= 0 || !colour.HasSingleColour)
                    return false;

                if (renderer.CurrentDepthInfo.DepthTest || !renderer.TargetHasDepth)
                    return false;

                if (!ReferenceEquals(bandsGradient, gradient))
                {
                    bands = fitBands(gradient);
                    bandsGradient = gradient;
                }

                if (bands == null || !buildLine())
                    return false;

                return line.Count == 0 || tryTakeDepth(renderer, out depth);
            }

            /// <summary>
            /// Collects the visible points of the path, without repeats.
            /// </summary>
            private bool buildLine()
            {
                line.Clear();

                if (drawRange is { } range)
                {
                    if (range.FirstVertex + range.VertexCount > points.Count)
                        return false;

                    addPoint(range.StartPoint);
                    for (int i = 0; i < range.VertexCount; i++)
                        addPoint(points[range.FirstVertex + i]);
                    addPoint(range.EndPoint);
                }
                else if (points.Count > 1)
                {
                    foreach (var point in points)
                        addPoint(point);
                }

                return true;

                void addPoint(Vector2 point)
                {
                    if (line.Count == 0 || (point - line[^1]).LengthSquared >= precision * precision)
                        line.Add(point);
                }
            }

            /// <summary>
            /// Adds the region at distances from <see cref="MeshBuilder.Inner"/> to <see cref="MeshBuilder.Outer"/> around the line:
            /// two quads per segment, a rounded wedge on the outer side of each join, and rounded caps at the ends.
            /// </summary>
            private void drawBand(ref MeshBuilder mesh)
            {
                if (line.Count == 1)
                {
                    mesh.AddArc(line[0], Vector2.UnitY, Vector2.UnitY, MathF.Tau);
                    return;
                }

                for (int i = 0; i < directions.Count; i++)
                {
                    Vector2 start = line[i];
                    Vector2 end = line[i + 1];
                    Vector2 direction = directions[i];
                    Vector2 normal = new Vector2(-direction.Y, direction.X);

                    mesh.AddSide(start, end, normal);
                    mesh.AddSide(start, end, -normal);

                    if (i == 0)
                    {
                        mesh.AddArc(start, normal, -normal, MathF.PI);
                        continue;
                    }

                    Vector2 previous = directions[i - 1];
                    float turn = MathF.Atan2(previous.X * direction.Y - previous.Y * direction.X, Vector2.Dot(previous, direction));

                    // The gap between consecutive segments is on the side away from the turn.
                    if (MathF.Abs(turn) > 1e-4f)
                    {
                        Vector2 previousNormal = new Vector2(-previous.Y, previous.X);
                        mesh.AddArc(start, turn > 0 ? -previousNormal : previousNormal, turn > 0 ? -normal : normal, turn);
                    }
                }

                Vector2 last = directions[^1];
                Vector2 lastNormal = new Vector2(-last.Y, last.X);
                mesh.AddArc(line[^1], -lastNormal, lastNormal, MathF.PI);
            }

            private static Color4 multiply(Color4 a, Color4 b) => new Color4(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

            [ThreadStatic]
            private static IRenderer? depthRenderer;

            [ThreadStatic]
            private static ulong depthFrame;

            [ThreadStatic]
            private static int depthCount;

            /// <summary>
            /// Takes a depth for this path in the current frame. Depths decrease from the clear value of 1, so that each path passes the
            /// depth test over paths drawn before it, as in the framebuffer composite, and fails against its own earlier bands.
            /// Each renderer draws on its own thread.
            /// </summary>
            private static bool tryTakeDepth(IRenderer renderer, out float depth)
            {
                if (depthRenderer != renderer || depthFrame != renderer.FrameIndex)
                {
                    depthRenderer = renderer;
                    depthFrame = renderer.FrameIndex;
                    depthCount = 0;
                }

                depth = 1 - ++depthCount * depth_step;
                return depth > -1;
            }

            /// <summary>
            /// Splits the gradient into bands with linear colour across distance, from the centre outwards.
            /// </summary>
            /// <returns>The bands, or null if the gradient needs more than <see cref="max_bands"/>.</returns>
            private static Band[]? fitBands(Color4[] gradient)
            {
                int width = gradient.Length;
                if (width < 2)
                    return null;

                // The framebuffer composite samples the gradient as a texture with linear filtering, from the outermost point (index 0) to the
                // centre (last index), so colours sit at texel centres and stay constant beyond the centre texel. Order them from the centre outwards.
                float[] distances = new float[width + 1];
                var colours = new Color4[width + 1];
                colours[0] = gradient[^1];

                for (int i = 1; i <= width; i++)
                {
                    distances[i] = 1 - (width - i + 0.5f) / width;
                    colours[i] = gradient[width - i];
                }

                var result = new List<Band>();

                for (int start = 0; start < width;)
                {
                    int end = start + 1;
                    while (end < width && fits(distances, colours, start, end + 1))
                        end++;

                    if (result.Count == max_bands)
                        return null;

                    result.Add(new Band(distances[start], distances[end], colours[start], colours[end]));
                    start = end;
                }

                return result.ToArray();
            }

            private static bool fits(float[] distances, Color4[] colours, int start, int end)
            {
                Color4 a = colours[start];
                Color4 b = colours[end];

                if (difference(a, b) > max_band_variation)
                    return false;

                for (int i = start + 1; i < end; i++)
                {
                    float t = (distances[i] - distances[start]) / (distances[end] - distances[start]);
                    var interpolated = new Color4(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

                    if (difference(interpolated, colours[i]) > fit_tolerance)
                        return false;
                }

                return true;
            }

            /// <summary>
            /// The largest channel difference, with colour weighted by opacity so that colour changes where the path is transparent do not count.
            /// </summary>
            private static float difference(Color4 a, Color4 b)
            {
                float opacity = Math.Max(a.A, b.A);
                float colour = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                return Math.Max(Math.Abs(a.A - b.A), colour * opacity);
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                buffered?.Dispose();
                buffered = null!;

                batch?.Dispose();
            }

            /// <summary>
            /// A range of distances from the line, as fractions of the radius, with colour interpolated linearly between its edges.
            /// </summary>
            private readonly record struct Band(float Inner, float Outer, Color4 InnerColour, Color4 OuterColour);

            /// <summary>
            /// Adds quads of the current band in screen space to a vertex list.
            /// </summary>
            private struct MeshBuilder
            {
                public float Inner;
                public float Outer;
                public Color4 InnerColour;
                public Color4 OuterColour;
                public float MaxArcStep;

                private readonly List<TexturedVertex2D> vertices;
                private readonly Matrix3 matrix;
                private TexturedVertex2D vertex;

                public MeshBuilder(List<TexturedVertex2D> vertices, TexturedVertex2D vertex, Matrix3 matrix)
                {
                    this.vertices = vertices;
                    this.vertex = vertex;
                    this.matrix = matrix;
                    Inner = Outer = MaxArcStep = 0;
                    InnerColour = OuterColour = default;
                }

                /// <summary>
                /// Adds the band along one side of a segment.
                /// </summary>
                public void AddSide(Vector2 start, Vector2 end, Vector2 normal)
                {
                    add(start + normal * Inner, InnerColour);
                    add(end + normal * Inner, InnerColour);
                    add(end + normal * Outer, OuterColour);
                    add(start + normal * Outer, OuterColour);
                }

                /// <summary>
                /// Adds the band around <paramref name="centre"/>, from the direction <paramref name="from"/> rotated by <paramref name="angle"/>
                /// to <paramref name="to"/>. The last step ends exactly on <paramref name="to"/>, where the next quads begin.
                /// </summary>
                public void AddArc(Vector2 centre, Vector2 from, Vector2 to, float angle)
                {
                    int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(angle) / MaxArcStep));
                    float step = angle / steps;
                    float cos = MathF.Cos(step);
                    float sin = MathF.Sin(step);

                    for (int i = 0; i < steps; i++)
                    {
                        Vector2 next = i == steps - 1 ? to : new Vector2(from.X * cos - from.Y * sin, from.X * sin + from.Y * cos);

                        add(centre + from * Inner, InnerColour);
                        add(centre + from * Outer, OuterColour);
                        add(centre + next * Outer, OuterColour);
                        add(centre + next * Inner, InnerColour);

                        from = next;
                    }
                }

                private void add(Vector2 position, Color4 colour)
                {
                    vertex.Position = Vector2Extensions.Transform(position, matrix);
                    vertex.Colour = colour;
                    vertices.Add(vertex);
                }
            }
        }
    }
}
