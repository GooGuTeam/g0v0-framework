// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;
using osuTK;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Allocation;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Caching;
using osu.Framework.Configuration;
using osu.Framework.Extensions.EnumExtensions;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders.Types;
using osu.Framework.Layout;
using osuTK.Graphics;

namespace osu.Framework.Graphics.Lines
{
    public partial class Path : Drawable, IBufferedDrawable
    {
        /// <summary>
        /// Whether paths that provide a <see cref="DirectGradient"/> are drawn straight into the current target as meshes, instead of through a framebuffer.
        /// Experimental; enabled through the DirectPathRendering framework setting at startup.
        /// </summary>
        internal static bool DirectDrawingEnabled => FrameworkFeatureFlags.DirectPathRendering;

        public IShader TextureShader { get; private set; }
        private IShader pathShader;
        private IShader directShader;

        [Resolved]
        private IRenderer renderer { get; set; }

        public Path()
        {
            AutoSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            TextureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, "Path");
            pathShader = shaders.Load("PathPrepass", "PathPrepass");

            if (DirectDrawingEnabled)
                directShader = shaders.Load("PathDirect", "Texture");
        }

        /// <summary>
        /// The colours across the path's width for drawing without a framebuffer, from the outermost point (first) to the centre (last).
        /// Null always draws through the framebuffer. A new array must be assigned for each change, as draw nodes keep the previous one.
        /// </summary>
        private protected virtual Color4[] DirectGradient => null;

        private readonly List<Vector2> vertices = new List<Vector2>();

        public IReadOnlyList<Vector2> Vertices
        {
            get => vertices;
            set
            {
                vertices.Clear();
                vertices.AddRange(value);
                drawRange = null;

                vertexBoundsCache.Invalidate();
                segmentsCache.Invalidate();

                Invalidate(Invalidation.DrawSize);
            }
        }

        private PathDrawRange? drawRange;

        /// <summary>
        /// Restricts rendering and hit testing without changing the full path or its bounding box.
        /// Null draws the complete path. Changing vertices resets this range.
        /// </summary>
        public PathDrawRange? DrawRange
        {
            get => drawRange;
            set
            {
                if (value is { } range && (range.FirstVertex < 0 || range.VertexCount < 0
                    || range.FirstVertex > vertices.Count - range.VertexCount))
                    throw new ArgumentOutOfRangeException(nameof(value));

                if (drawRange == value)
                    return;

                drawRange = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private float pathRadius = 10f;

        /// <summary>
        /// How wide this path is on each side of the line.
        /// </summary>
        /// <remarks>
        /// The actual width of the path is twice the PathRadius.
        /// </remarks>
        public virtual float PathRadius
        {
            get => pathRadius;
            set
            {
                if (pathRadius == value) return;

                pathRadius = value;

                vertexBoundsCache.Invalidate();
                segmentsCache.Invalidate();

                Invalidate(Invalidation.DrawSize);
            }
        }

        public override Axes RelativeSizeAxes
        {
            get => base.RelativeSizeAxes;
            set
            {
                if ((AutoSizeAxes & value) != 0)
                    throw new InvalidOperationException("No axis can be relatively sized and automatically sized at the same time.");

                base.RelativeSizeAxes = value;
            }
        }

        private Axes autoSizeAxes;

        /// <summary>
        /// Controls which <see cref="Axes"/> are automatically sized w.r.t. the bounds of the vertices.
        /// It is not allowed to manually set <see cref="Size"/> (or <see cref="Width"/> / <see cref="Height"/>)
        /// on any <see cref="Axes"/> which are automatically sized.
        /// </summary>
        public virtual Axes AutoSizeAxes
        {
            get => autoSizeAxes;
            set
            {
                if (value == autoSizeAxes)
                    return;

                if ((RelativeSizeAxes & value) != 0)
                    throw new InvalidOperationException("No axis can be relatively sized and automatically sized at the same time.");

                autoSizeAxes = value;
                OnSizingChanged();
            }
        }

        public override float Width
        {
            get
            {
                if (AutoSizeAxes.HasFlagFast(Axes.X))
                    return base.Width = vertexBounds.Width;

                return base.Width;
            }
            set
            {
                if ((AutoSizeAxes & Axes.X) != 0)
                    throw new InvalidOperationException($"The width of a {nameof(Path)} with {nameof(AutoSizeAxes)} can not be set manually.");

                base.Width = value;
            }
        }

        public override float Height
        {
            get
            {
                if (AutoSizeAxes.HasFlagFast(Axes.Y))
                    return base.Height = vertexBounds.Height;

                return base.Height;
            }
            set
            {
                if ((AutoSizeAxes & Axes.Y) != 0)
                    throw new InvalidOperationException($"The height of a {nameof(Path)} with {nameof(AutoSizeAxes)} can not be set manually.");

                base.Height = value;
            }
        }

        public override Vector2 Size
        {
            get
            {
                if (AutoSizeAxes != Axes.None)
                    return base.Size = vertexBounds.Size;

                return base.Size;
            }
            set
            {
                if ((AutoSizeAxes & Axes.Both) != 0)
                    throw new InvalidOperationException($"The Size of a {nameof(Path)} with {nameof(AutoSizeAxes)} can not be set manually.");

                base.Size = value;
            }
        }

        private readonly Cached<RectangleF> vertexBoundsCache = new Cached<RectangleF>();

        private RectangleF vertexBounds
        {
            get
            {
                if (vertexBoundsCache.IsValid)
                    return vertexBoundsCache.Value;

                if (vertices.Count > 0)
                {
                    float minX = 0;
                    float minY = 0;
                    float maxX = 0;
                    float maxY = 0;

                    foreach (var v in vertices)
                    {
                        minX = Math.Min(minX, v.X - PathRadius);
                        minY = Math.Min(minY, v.Y - PathRadius);
                        maxX = Math.Max(maxX, v.X + PathRadius);
                        maxY = Math.Max(maxY, v.Y + PathRadius);
                    }

                    return vertexBoundsCache.Value = new RectangleF(minX, minY, maxX - minX, maxY - minY);
                }

                return vertexBoundsCache.Value = new RectangleF(0, 0, 0, 0);
            }
        }

        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos)
        {
            var localPos = ToLocalSpace(screenSpacePos);
            float pathRadiusSquared = PathRadius * PathRadius;

            if (drawRange is { } range)
            {
                Vector2 offset = vertexBounds.TopLeft;
                Vector2 previous = range.StartPoint - offset;
                for (int i = range.FirstVertex; i < range.FirstVertex + range.VertexCount; i++)
                {
                    Vector2 next = vertices[i] - offset;
                    if (new Line(previous, next).DistanceSquaredToPoint(localPos) <= pathRadiusSquared)
                        return true;
                    previous = next;
                }

                return new Line(previous, range.EndPoint - offset).DistanceSquaredToPoint(localPos) <= pathRadiusSquared;
            }

            foreach (var t in segments)
            {
                if (t.DistanceSquaredToPoint(localPos) <= pathRadiusSquared)
                    return true;
            }

            return false;
        }

        public Vector2 PositionInBoundingBox(Vector2 pos) => pos - vertexBounds.TopLeft;

        public void ClearVertices()
        {
            if (vertices.Count == 0)
                return;

            vertices.Clear();
            drawRange = null;

            vertexBoundsCache.Invalidate();
            segmentsCache.Invalidate();

            Invalidate(Invalidation.DrawSize);
        }

        public void AddVertex(Vector2 pos)
        {
            vertices.Add(pos);
            drawRange = null;

            vertexBoundsCache.Invalidate();
            segmentsCache.Invalidate();

            Invalidate(Invalidation.DrawSize);
        }

        public void ReplaceVertex(int index, Vector2 pos)
        {
            vertices[index] = pos;
            drawRange = null;

            vertexBoundsCache.Invalidate();
            segmentsCache.Invalidate();

            Invalidate(Invalidation.DrawSize);
        }

        private readonly List<Line> segmentsBacking = new List<Line>();
        private readonly Cached segmentsCache = new Cached();
        private long segmentsVersion;
        private List<Line> segments => segmentsCache.IsValid ? segmentsBacking : generateSegments();

        private List<Line> generateSegments()
        {
            segmentsBacking.Clear();

            if (vertices.Count > 1)
            {
                Vector2 offset = vertexBounds.TopLeft;
                for (int i = 0; i < vertices.Count - 1; ++i)
                    segmentsBacking.Add(new Line(vertices[i] - offset, vertices[i + 1] - offset));
            }

            segmentsVersion++;
            segmentsCache.Validate();
            return segmentsBacking;
        }

        private Texture texture;

        protected Texture Texture
        {
            get => texture ?? renderer?.WhitePixel;
            set
            {
                if (texture == value)
                    return;

                texture?.Dispose();
                texture = value;

                Invalidate(Invalidation.DrawNode);
            }
        }

        public DrawColourInfo? FrameBufferDrawColour => base.DrawColourInfo;

        private Vector2 frameBufferScale = Vector2.One;

        public Vector2 FrameBufferScale
        {
            get => frameBufferScale;
            set
            {
                if (frameBufferScale == value)
                    return;

                frameBufferScale = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        // The path should not receive the true colour to avoid colour doubling when the frame-buffer is rendered to the back-buffer.
        public override DrawColourInfo DrawColourInfo => new DrawColourInfo(Color4.White, base.DrawColourInfo.Blending);

        private static readonly Color4 background_colour = new Color4(0, 0, 0, 0);

        /// <summary>
        /// The background colour to be used for the frame buffer this path is rendered to.
        /// </summary>
        public Color4 BackgroundColour => background_colour;

        public long PathInvalidationID { get; private set; }

        protected override bool OnInvalidate(Invalidation invalidation, InvalidationSource source)
        {
            bool result = base.OnInvalidate(invalidation, source);

            // Colour is being applied to the buffer instead of the actual drawable, thus removing the need to redraw the path on colour invalidation.
            invalidation &= ~Invalidation.Colour;

            if (invalidation != Invalidation.None)
                PathInvalidationID++;

            return result;
        }

        private TexturePixelFormat textureFormat = TexturePixelFormat.R8Float;

        public TexturePixelFormat TextureFormat
        {
            get => textureFormat;
            set
            {
                if (textureFormat == value)
                    return;

                textureFormat = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private readonly BufferedDrawNodeSharedData sharedData = new BufferedDrawNodeSharedData(TexturePixelFormat.R8Float, clipToRootNode: true);

        protected override DrawNode CreateDrawNode()
        {
            var buffered = new PathBufferedDrawNode(this, new PathDrawNode(this), sharedData);
            return DirectDrawingEnabled ? new PathDirectDrawNode(this, buffered) : buffered;
        }

        private class PathBufferedDrawNode : BufferedDrawNode
        {
            protected new Path Source => (Path)base.Source;

            public PathBufferedDrawNode(Path source, PathDrawNode child, BufferedDrawNodeSharedData sharedData)
                : base(source, child, sharedData)
            {
            }

            private long pathInvalidationID = -1;
            private Texture texture;
            private Vector4 textureRect;
            private IUniformBuffer<PathTextureParameters> parametersBuffer;

            public override void ApplyState()
            {
                base.ApplyState();
                pathInvalidationID = Source.PathInvalidationID;
                texture = Source.Texture;
                SharedData.TextureFormat = Source.TextureFormat;

                var rect = texture.GetTextureRect();
                textureRect = new Vector4(rect.Left, rect.Top, rect.Width, rect.Height);
            }

            protected override void BindUniformResources(IShader shader, IRenderer renderer)
            {
                base.BindUniformResources(shader, renderer);

                parametersBuffer ??= renderer.CreateUniformBuffer<PathTextureParameters>();
                parametersBuffer.Data = new PathTextureParameters
                {
                    TexRect1 = textureRect,
                };
                shader.BindUniformBlock("m_PathTextureParameters", parametersBuffer);

                texture?.Bind(1);
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private record struct PathTextureParameters
            {
                public UniformVector4 TexRect1;
            }

            protected override long GetDrawVersion() => pathInvalidationID;

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                parametersBuffer?.Dispose();
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            texture?.Dispose();
            texture = null;

            sharedData.Dispose();
        }
    }
}
