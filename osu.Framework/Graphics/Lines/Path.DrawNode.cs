// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using osu.Framework.Graphics.Primitives;
using osuTK;
using System;
using System.Collections.Generic;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using System.Diagnostics;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Shaders.Types;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.Lines
{
    public partial class Path
    {
        private class PathDrawNode : DrawNode
        {
            private const float precision = 0.01f; // Smallest allowed segment length. Used for segment reduction algorithm.
            private const int min_batch_quads = 64;
            private const int max_batch_quads = 9000;
            private const int vertices_per_chunk = 64 * IRenderer.VERTICES_PER_QUAD;

            protected new Path Source => (Path)base.Source;

            private readonly List<Line> segments = new List<Line>();
            private long segmentsVersion = -1;
            private readonly List<PathVertex> vertices = new List<PathVertex>();
            private readonly List<RectangleF> vertexChunkBounds = new List<RectangleF>();
            private bool verticesValid;
            private StaticQuadMesh<PathVertex>? staticMesh;
            private PathDrawRange? drawRange;
            private Vector2 firstRangeVertex;
            private Vector2 lastRangeVertex;
            private readonly List<PathVertex> endpointVertices = new List<PathVertex>(8);
            private IVertexBatch<PathVertex>? endpointBatch;

            private float radius;
            private IShader? pathShader;
            private IUniformBuffer<PathParameters>? parametersBuffer;

            private IVertexBatch<PathVertex>? quadBatch;
            private int batchQuadCapacity;

            public PathDrawNode(Path source)
                : base(source)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();

                // Transforms and texture changes may redraw the path without changing its local-space segments.
                // Resolve the lazy segment cache before comparing its version.
                var sourceSegments = Source.segments;
                if (segmentsVersion != Source.segmentsVersion)
                {
                    segments.Clear();
                    segments.AddRange(sourceSegments);
                    segmentsVersion = Source.segmentsVersion;
                    verticesValid = false;
                }

                if (radius != Source.PathRadius)
                    verticesValid = false;

                radius = Source.PathRadius;
                pathShader = Source.pathShader;

                if (drawRange.HasValue != Source.DrawRange.HasValue)
                    verticesValid = false;

                drawRange = Source.DrawRange;
                if (drawRange is { } range)
                {
                    Vector2 offset = Source.vertexBounds.TopLeft;
                    drawRange = range with { StartPoint = range.StartPoint - offset, EndPoint = range.EndPoint - offset };
                    if (range.VertexCount > 0)
                    {
                        firstRangeVertex = Source.vertices[range.FirstVertex] - offset;
                        lastRangeVertex = Source.vertices[range.FirstVertex + range.VertexCount - 1] - offset;
                    }
                }
            }

            protected override void Draw(IRenderer renderer)
            {
                base.Draw(renderer);

                if ((segments.Count == 0 && drawRange == null) || pathShader == null || radius == 0f)
                    return;

                if (!verticesValid)
                {
                    staticMesh?.Dispose();
                    staticMesh = null;
                    vertices.Clear();
                    if (drawRange.HasValue)
                    {
                        // Preserve original segment indices for range draws. Interior joins retain the reduced
                        // caps to avoid overdraw; the dynamic boundary segments supply complete endpoint discs.
                        for (int i = 0; i < segments.Count; i++)
                        {
                            var segment = segments[i];
                            Vector2 previous = i > 0 ? segments[i - 1].Direction : segment.Direction;
                            bool shortSegment = segment.Direction.LengthSquared < precision;
                            addSegmentVertices(segment, previous, i == 0 || shortSegment ? SegmentStartLocation.Outside : SegmentStartLocation.End,
                                i == segments.Count - 1 || shortSegment);
                        }
                    }
                    else
                        generateVertices();
                    updateVertexChunkBounds();
                    verticesValid = true;
                }

                // Grow in buckets rather than reallocating for each new segment while snaking.
                // Keep the previous upper bound and overflow behaviour for very large paths.
                int requiredQuads = Math.Min(vertices.Count / IRenderer.VERTICES_PER_QUAD, max_batch_quads);
                if (!drawRange.HasValue && (quadBatch == null || batchQuadCapacity < requiredQuads))
                {
                    int capacity = min_batch_quads;
                    while (capacity < requiredQuads)
                        capacity = Math.Min(capacity * 2, max_batch_quads);

                    quadBatch?.Dispose();
                    quadBatch = renderer.CreateQuadBatch<PathVertex>(capacity, 10);
                    // Deferred batches have effectively unbounded capacity and do not need replacement as the path grows.
                    batchQuadCapacity = Math.Max(capacity, quadBatch.Size / IRenderer.VERTICES_PER_QUAD);
                }

                renderer.PushLocalMatrix(DrawInfo.Matrix);

                renderer.SetBlend(new BlendingParameters
                {
                    Source = BlendingType.One,
                    Destination = BlendingType.One,
                    SourceAlpha = BlendingType.One,
                    DestinationAlpha = BlendingType.One,
                    RGBEquation = BlendingEquation.Max,
                    AlphaEquation = BlendingEquation.Max,
                });

                pathShader.Bind();

                parametersBuffer ??= renderer.CreateUniformBuffer<PathParameters>();
                parametersBuffer.Data = new PathParameters
                {
                    Radius = radius,
                };
                pathShader.BindUniformBlock("m_PathParameters", parametersBuffer);

                if (drawRange is { } range)
                    drawStaticRange(renderer, range);
                else
                    drawDynamicMesh(renderer);

                pathShader.Unbind();

                renderer.PopLocalMatrix();
            }

            private void drawDynamicMesh(IRenderer renderer)
            {
                Debug.Assert(quadBatch != null);
                RectangleI frameBounds = renderer.CurrentMaskingInfo.ScreenSpaceAABB;
                for (int chunk = 0; chunk < vertexChunkBounds.Count; chunk++)
                {
                    // Test complete quads rather than endpoints: a segment can cross the viewport with both ends outside.
                    // Keep a small screen-space margin for rasterisation and floating-point rounding at the framebuffer edge.
                    RectangleF screenBounds = (Quad.FromRectangle(vertexChunkBounds[chunk]) * DrawInfo.Matrix).AABBFloat.Inflate(2);
                    if (!screenBounds.IntersectsWith(frameBounds))
                        continue;

                    int start = chunk * vertices_per_chunk;
                    int end = Math.Min(start + vertices_per_chunk, vertices.Count);
                    for (int i = start; i < end; i++)
                        quadBatch.Add(vertices[i]);
                }
            }

            private void drawStaticRange(IRenderer renderer, PathDrawRange range)
            {
                if (vertices.Count > 0)
                    staticMesh ??= StaticQuadMesh<PathVertex>.Create(renderer, CollectionsMarshal.AsSpan(vertices));

                int first = range.FirstVertex * IRenderer.VERTICES_PER_QUAD;
                int end = first + Math.Max(0, range.VertexCount - 1) * IRenderer.VERTICES_PER_QUAD;
                int runStart = -1;
                int runEnd = first;
                RectangleI frameBounds = renderer.CurrentMaskingInfo.ScreenSpaceAABB;

                for (int start = first; start < end;)
                {
                    int chunk = start / vertices_per_chunk;
                    int chunkEnd = Math.Min(end, (chunk + 1) * vertices_per_chunk);
                    RectangleF screenBounds = (Quad.FromRectangle(vertexChunkBounds[chunk]) * DrawInfo.Matrix).AABBFloat.Inflate(2);
                    if (screenBounds.IntersectsWith(frameBounds))
                    {
                        if (runStart < 0)
                            runStart = start;
                        runEnd = chunkEnd;
                    }
                    else if (runStart >= 0)
                    {
                        staticMesh!.Draw(runStart, runEnd - runStart);
                        runStart = -1;
                    }
                    start = chunkEnd;
                }

                if (runStart >= 0)
                    staticMesh!.Draw(runStart, runEnd - runStart);

                // Only the two partial boundary segments change as the slider snakes.
                endpointVertices.Clear();
                if (range.VertexCount > 0)
                {
                    addSegmentVertices(new Line(range.StartPoint, firstRangeVertex), Vector2.Zero, SegmentStartLocation.Outside, true, endpointVertices);
                    addSegmentVertices(new Line(lastRangeVertex, range.EndPoint), Vector2.Zero, SegmentStartLocation.Outside, true, endpointVertices);
                }
                else
                    addSegmentVertices(new Line(range.StartPoint, range.EndPoint), Vector2.Zero, SegmentStartLocation.Outside, true, endpointVertices);

                endpointBatch ??= renderer.CreateQuadBatch<PathVertex>(2, 10);
                foreach (var vertex in endpointVertices)
                    endpointBatch.Add(vertex);
            }

            /// <summary>
            /// Appends the quad for a segment, including the caps needed for a visually continuous path.
            /// </summary>
            /// <param name="segmentToDraw">The segment to be drawn.</param>
            /// <param name="prevSegmentDirection">Direction of a previous segment. Used to compute angle between segments.</param>
            /// <param name="location">Position of the segment's start point relative to the previous segment.</param>
            /// <param name="endCap">Whether end cap of this segment must be drawn.</param>
            /// <param name="target">The destination vertex list, or null for the main mesh.</param>
            private void addSegmentVertices(Line segmentToDraw, Vector2 prevSegmentDirection, SegmentStartLocation location, bool endCap, List<PathVertex>? target = null)
            {
                target ??= vertices;
                Vector2 dir = segmentToDraw.Direction;
                float lengthSquared = dir.X * dir.X + dir.Y * dir.Y;
                Vector2 dirNorm = lengthSquared < precision * precision ? Vector2.UnitX : (dir / MathF.Sqrt(lengthSquared));
                Vector2 thicknessOffset = new Vector2(-dirNorm.Y, dirNorm.X) * radius;

                Vector2 capOffset = dirNorm * radius;
                Vector2 endOffset = endCap ? capOffset : Vector2.Zero;

                // Always assume start cap to act as a form of connection between segments.
                // The code below will manipulate its length to be as low as possible for each case to reduce overdraw.
                Vector2 startOffset = capOffset;

                // Segment starts from "inside" the previous one, the cap will be covered by the previous segment anyway.
                if (location == SegmentStartLocation.Inside)
                {
                    startOffset = Vector2.Zero;
                }
                else if (location == SegmentStartLocation.End) // Segment starts at the end of the previous one
                {
                    Vector2 dir2 = -prevSegmentDirection;
                    Vector2.Dot(ref dir, ref dir2, out float dot);

                    // if angle is more than 90 degrees we can reduce start cap length just enough for hole to be covered
                    if (dot < 0)
                    {
                        Vector2.PerpDot(ref dir, ref dir2, out float pDot);
                        float thetaDiff = Math.Abs(MathF.Atan(pDot / dot));
                        startOffset *= (float)Math.Sin(thetaDiff);
                    }
                }

                // Clockwise order: topLeft, topRight, bottomRight, bottomLeft.
                target.Add(new PathVertex(segmentToDraw.StartPoint + thicknessOffset - startOffset, segmentToDraw.StartPoint, segmentToDraw.EndPoint));
                target.Add(new PathVertex(segmentToDraw.EndPoint + thicknessOffset + endOffset, segmentToDraw.StartPoint, segmentToDraw.EndPoint));
                target.Add(new PathVertex(segmentToDraw.EndPoint - thicknessOffset + endOffset, segmentToDraw.StartPoint, segmentToDraw.EndPoint));
                target.Add(new PathVertex(segmentToDraw.StartPoint - thicknessOffset - startOffset, segmentToDraw.StartPoint, segmentToDraw.EndPoint));
            }

            private void generateVertices()
            {
                Debug.Assert(segments.Count > 0);

                Line segmentToDraw = segments[0];

                // location of the current segment relative to the previous one.
                SegmentStartLocation location = SegmentStartLocation.Outside;
                SegmentStartLocation nextLocation = SegmentStartLocation.End;

                // Use a matching initial direction so that the first segment receives a start cap.
                Vector2 lastSegmentDirection = segmentToDraw.Direction;

                // This loop tries to merge consecutive path segments which are located within the same line.
                for (int i = 1; i < segments.Count; i++)
                {
                    Vector2 dir = segmentToDraw.Direction;
                    float lengthSquared = dir.X * dir.X + dir.Y * dir.Y;
                    Vector2 nextVertex = segments[i].EndPoint;

                    // If segment is too short, connect it to the next vertex directly.
                    if (lengthSquared < precision)
                    {
                        segmentToDraw = new Line(segmentToDraw.StartPoint, nextVertex);
                        continue;
                    }

                    Vector2 dir2 = nextVertex - segmentToDraw.StartPoint;
                    Vector2.PerpDot(ref dir, ref dir2, out float pDot);

                    // Expand segment if next vertex is located within a line passing through it (distance from the next vertex to the segment is less than precision)
                    if (pDot * pDot / lengthSquared < precision * precision)
                    {
                        nextLocation = SegmentStartLocation.Inside;

                        Vector2.Dot(ref dir, ref dir2, out float dot);

                        // new vertex is located behind the segment start point, expand segment backwards
                        if (dot < 0)
                        {
                            segmentToDraw = new Line(nextVertex, segmentToDraw.EndPoint);
                            location = SegmentStartLocation.Outside;
                        }
                        else if (dot > lengthSquared) // new vertex is located in front of the end point, expand segment forward
                        {
                            segmentToDraw = new Line(segmentToDraw.StartPoint, nextVertex);
                            nextLocation = SegmentStartLocation.End;
                        }
                    }
                    else // Otherwise draw the expanded segment
                    {
                        // if next segment starts inside the current one, nothing will be connected to the end of the current segment - end cap is required.
                        addSegmentVertices(segmentToDraw, lastSegmentDirection, location, nextLocation == SegmentStartLocation.Inside);

                        lastSegmentDirection = dir;
                        segmentToDraw = segments[i];
                        location = nextLocation;
                        nextLocation = SegmentStartLocation.End;
                    }
                }

                // Finish drawing last segment
                addSegmentVertices(segmentToDraw, lastSegmentDirection, location, true);
            }

            private void updateVertexChunkBounds()
            {
                vertexChunkBounds.Clear();

                for (int start = 0; start < vertices.Count; start += vertices_per_chunk)
                {
                    Vector2 min = vertices[start].Position;
                    Vector2 max = min;
                    int end = Math.Min(start + vertices_per_chunk, vertices.Count);

                    for (int i = start + 1; i < end; i++)
                    {
                        Vector2 position = vertices[i].Position;
                        min.X = Math.Min(min.X, position.X);
                        min.Y = Math.Min(min.Y, position.Y);
                        max.X = Math.Max(max.X, position.X);
                        max.Y = Math.Max(max.Y, position.Y);
                    }

                    vertexChunkBounds.Add(new RectangleF(min, max - min));
                }
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                quadBatch?.Dispose();
                endpointBatch?.Dispose();
                staticMesh?.Dispose();
                parametersBuffer?.Dispose();
            }

            private enum SegmentStartLocation
            {
                Inside,
                End,
                Outside
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private record struct PathParameters
            {
                public UniformFloat Radius;
                private UniformPadding12 pad;
            }

            [StructLayout(LayoutKind.Sequential)]
            public readonly struct PathVertex : IEquatable<PathVertex>, IVertex
            {
                [VertexMember(2, VertexAttribPointerType.Float)]
                public readonly Vector2 Position;

                [VertexMember(2, VertexAttribPointerType.Float)]
                public readonly Vector2 StartPos;

                [VertexMember(2, VertexAttribPointerType.Float)]
                public readonly Vector2 EndPos;

                public PathVertex(Vector2 position, Vector2 startPos, Vector2 endPos)
                {
                    Position = position;
                    StartPos = startPos;
                    EndPos = endPos;
                }

                public bool Equals(PathVertex other) =>
                    Position.Equals(other.Position)
                    && StartPos.Equals(other.StartPos)
                    && EndPos.Equals(other.EndPos);
            }
        }
    }
}
