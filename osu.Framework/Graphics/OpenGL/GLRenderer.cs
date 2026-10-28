// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using osu.Framework.Extensions;
using osu.Framework.Extensions.EnumExtensions;
using osu.Framework.Graphics.OpenGL.Batches;
using osu.Framework.Graphics.OpenGL.Buffers;
using osu.Framework.Graphics.OpenGL.Shaders;
using osu.Framework.Graphics.OpenGL.Textures;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using osuTK;
using osuTK.Graphics;
using osuTK.Graphics.ES30;
using osu.Framework.Imaging;
using GL4 = osuTK.Graphics.OpenGL;
using Image = osu.Framework.Imaging.Image;

namespace osu.Framework.Graphics.OpenGL
{
    internal class GLRenderer : Renderer
    {
        private IOpenGLGraphicsSurface openGLSurface = null!;

        protected internal override bool VerticalSync
        {
            get => openGLSurface.VerticalSync;
            set => openGLSurface.VerticalSync = value;
        }

        protected internal override bool AllowTearing { get; set; }

        public override bool IsDepthRangeZeroToOne => false;
        public override bool IsUvOriginTopLeft => false;
        public override bool IsClipSpaceYInverted => false;

        public bool UseStructuredBuffers { get; private set; }

        /// <summary>
        /// The maximum allowed render buffer size.
        /// </summary>
        public int MaxRenderBufferSize { get; private set; } = 4096; // default value is to allow roughly normal flow in cases we don't have a GL context, like headless CI.

        /// <summary>
        /// Whether the current platform is embedded.
        /// </summary>
        public bool IsEmbedded { get; private set; }

        private int backbufferFramebuffer;

        /// <summary>
        /// Whether linked programs can be retrieved and restored as driver binaries.
        /// </summary>
        internal bool SupportsProgramBinaries { get; private set; }

        private string programBinaryDriver = string.Empty;

        /// <summary>
        /// The cache key for a program's driver binary. Binaries are only valid for the driver that produced them,
        /// so the key includes the vendor, renderer and version strings. Version 1 binaries could contain a stage compiled
        /// for another program (see <see cref="GLShader"/>), so they are not reused.
        /// </summary>
        internal string? GetProgramBinaryKey(string vertexText, string fragmentText)
            => SupportsProgramBinaries ? $"{vertexText.ComputeMD5Hash()}#{fragmentText.ComputeMD5Hash()}#{programBinaryDriver}#glprogram#2" : null;

        /// <summary>
        /// Whether <see cref="TryGenerateMipmaps"/> may be used.
        /// </summary>
        internal bool SupportsRegionMipmaps { get; private set; }

        /// <summary>
        /// The largest region side processed regionally. Larger regions use whole-texture generation.
        /// </summary>
        private const int mipmap_scratch_size = 512;

        private int mipmapSourceFramebuffer;
        private readonly Dictionary<TextureComponentCount, MipmapScratch> mipmapScratches = new Dictionary<TextureComponentCount, MipmapScratch>();

        /// <summary>
        /// Regenerates mipmap levels 1 to <see cref="IRenderer.MAX_MIPMAP_LEVELS"/> for an aligned region of a texture,
        /// which must be bound on the active texture unit.
        /// <para>
        /// The region is read through a framebuffer into a pooled scratch texture, which GL generates mipmaps for, and the
        /// resulting levels are read back into the texture. Framebuffers are only read from (glCopyTexSubImage2D); nothing
        /// is rendered into mipmap levels. The scratch texture's level framebuffers are attached once, so a call only
        /// re-attaches the source texture.
        /// </para>
        /// </summary>
        /// <returns>False if the region could not be processed, in which case the whole texture must be regenerated.</returns>
        internal bool TryGenerateMipmaps(int textureId, RectangleI region, TextureComponentCount internalFormat, PixelFormat format, PixelType type)
        {
            MipmapRegions.ThrowIfMisaligned(region);

            if (region.Width > mipmap_scratch_size || region.Height > mipmap_scratch_size)
                return false;

            // Errors raised by earlier, unrelated calls must not be attributed to this sequence.
            for (int i = 0; i < 16 && GL.GetError() != ErrorCode.NoError; i++)
            {
            }

            if (mipmapSourceFramebuffer == 0)
                mipmapSourceFramebuffer = GL.GenFramebuffer();

            if (!mipmapScratches.TryGetValue(internalFormat, out var scratch))
                mipmapScratches[internalFormat] = scratch = new MipmapScratch(internalFormat, format, type);

            try
            {
                // Level 0 of the region into the scratch texture's top-left corner. Its mipmaps there depend only on that
                // corner, because the region is aligned.
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, mipmapSourceFramebuffer);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget2d.Texture2D, textureId, 0);

                GL.BindTexture(TextureTarget.Texture2D, scratch.Texture);
                GL.CopyTexSubImage2D(TextureTarget2d.Texture2D, 0, 0, 0, region.X, region.Y, region.Width, region.Height);

                // GL_GENERATE_MIPMAP_HINT does not exist in core profiles and would raise INVALID_ENUM; it is only a hint.
                GL.GenerateMipmap(TextureTarget.Texture2D);

                GL.BindTexture(TextureTarget.Texture2D, textureId);

                for (int level = 1; level <= IRenderer.MAX_MIPMAP_LEVELS; level++)
                {
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, scratch.LevelFramebuffers[level - 1]);
                    GL.CopyTexSubImage2D(TextureTarget2d.Texture2D, level, region.X >> level, region.Y >> level, 0, 0, region.Width >> level, region.Height >> level);
                }

                // An incomplete framebuffer or unsupported copy raises an error here, and the caller regenerates the whole texture.
                var error = GL.GetError();

                if (error == ErrorCode.NoError)
                    return true;

                Logger.Log($"Regional mipmap generation failed (GL error {error}); using whole-texture generation.", LoggingTarget.Performance);
                return false;
            }
            finally
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, mipmapSourceFramebuffer);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget2d.Texture2D, 0, 0);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, ((GLFrameBuffer?)FrameBuffer)?.FrameBuffer ?? backbufferFramebuffer);

                // Restore the target texture on the active unit, where the renderer expects it.
                GL.BindTexture(TextureTarget.Texture2D, textureId);
            }
        }

        /// <summary>
        /// A scratch texture with levels 0 to <see cref="IRenderer.MAX_MIPMAP_LEVELS"/>, and one framebuffer reading each of levels 1 and above.
        /// </summary>
        private sealed class MipmapScratch
        {
            public readonly int Texture;
            public readonly int[] LevelFramebuffers = new int[IRenderer.MAX_MIPMAP_LEVELS];

            public MipmapScratch(TextureComponentCount internalFormat, PixelFormat format, PixelType type)
            {
                Texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, Texture);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, IRenderer.MAX_MIPMAP_LEVELS);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)All.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)All.Nearest);

                for (int level = 0; level <= IRenderer.MAX_MIPMAP_LEVELS; level++)
                    GL.TexImage2D(TextureTarget2d.Texture2D, level, internalFormat, mipmap_scratch_size >> level, mipmap_scratch_size >> level, 0, format, type, IntPtr.Zero);

                for (int level = 1; level <= IRenderer.MAX_MIPMAP_LEVELS; level++)
                {
                    LevelFramebuffers[level - 1] = GL.GenFramebuffer();
                    GL.BindFramebuffer(FramebufferTarget.Framebuffer, LevelFramebuffers[level - 1]);
                    GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget2d.Texture2D, Texture, level);
                }
            }
        }

        private readonly Dictionary<string, IGLUniformBuffer> boundUniformBuffers = new Dictionary<string, IGLUniformBuffer>();
        private bool? lastBlendingEnabledState;
        private int lastBoundVertexArray;

        protected override void Initialise(IGraphicsSurface graphicsSurface)
        {
            if (graphicsSurface.Type != GraphicsSurfaceType.OpenGL)
                throw new InvalidOperationException($"{nameof(GLRenderer)} only supports OpenGL graphics surfaces.");

            openGLSurface = (IOpenGLGraphicsSurface)graphicsSurface;
            openGLSurface.MakeCurrent(openGLSurface.WindowContext);

            backbufferFramebuffer = openGLSurface.BackbufferFramebuffer ?? 0;

            string version = GL.GetString(StringName.Version);
            IsEmbedded = version.Contains("OpenGL ES"); // As defined by https://www.khronos.org/registry/OpenGL-Refpages/es2.0/xhtml/glGetString.xml

            MaxTextureSize = GL.GetInteger(GetPName.MaxTextureSize);

            // Program binaries are core in OpenGL ES 3.0 and OpenGL 4.1 (ARB_get_program_binary before that); drivers may still offer no formats.
            SupportsProgramBinaries = GL.GetInteger(GetPName.NumProgramBinaryFormats) > 0;
            GL.GetError();
            programBinaryDriver = $"{GL.GetString(StringName.Vendor)}|{GL.GetString(StringName.Renderer)}|{GL.GetString(StringName.Version)}".ComputeMD5Hash();

            // Attaching non-zero mipmap levels to framebuffers requires OpenGL 3.0 / OpenGL ES 3.0.
            // MAJOR_VERSION is not queryable before those versions; an error leaves the result at zero.
            SupportsRegionMipmaps = GL.GetInteger(GetPName.MajorVersion) >= 3;
            GL.GetError();
            MaxRenderBufferSize = GL.GetInteger(GetPName.MaxRenderbufferSize);

            GL.Disable(EnableCap.StencilTest);
            GL.Enable(EnableCap.Blend);

            string extensions = GetExtensions();

            Logger.Log($@"GL Initialized
                        GL Version:                 {GL.GetString(StringName.Version)}
                        GL Renderer:                {GL.GetString(StringName.Renderer)}
                        GL Shader Language version: {GL.GetString(StringName.ShadingLanguageVersion)}
                        GL Vendor:                  {GL.GetString(StringName.Vendor)}
                        GL Extensions:              {extensions}");

            UseStructuredBuffers = extensions.Contains(@"GL_ARB_shader_storage_buffer_object") && !FrameworkEnvironment.NoStructuredBuffers;

            Logger.Log($"{nameof(UseStructuredBuffers)}: {UseStructuredBuffers}");

            ProbeTextureSizes($"{GL.GetString(StringName.Vendor)}|{GL.GetString(StringName.Renderer)}|{version}", tryAllocateTexture);

            ProbeBindless($"{GL.GetString(StringName.Vendor)}|{GL.GetString(StringName.Renderer)}|{version}|{GL.GetString(StringName.ShadingLanguageVersion)}|{extensions}",
                () => GLBindlessTextureProbe.Run(openGLSurface, extensions));

            openGLSurface.ClearCurrent();
        }

        /// <summary>
        /// Allocates a render target of the given size and clears it, so that drivers which allocate lazily commit the memory.
        /// </summary>
        private bool tryAllocateTexture(int size, TexturePixelFormat format)
        {
            bool singleChannel = format == TexturePixelFormat.R8Float;
            int texture = 0;
            int framebuffer = 0;

            while (GL.GetError() != ErrorCode.NoError)
            {
            }

            try
            {
                texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexImage2D(TextureTarget2d.Texture2D, 0, singleChannel ? TextureComponentCount.R8 : TextureComponentCount.Rgba8, size, size, 0,
                    singleChannel ? PixelFormat.Red : PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

                if (GL.GetError() != ErrorCode.NoError)
                    return false;

                framebuffer = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget2d.Texture2D, texture, 0);

                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                    return false;

                GL.Viewport(0, 0, size, size);
                GL.ClearColor(0, 0, 0, 0);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.Finish();

                return GL.GetError() == ErrorCode.NoError;
            }
            finally
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, backbufferFramebuffer);
                GL.BindTexture(TextureTarget.Texture2D, 0);

                if (framebuffer != 0)
                    GL.DeleteFramebuffer(framebuffer);
                if (texture != 0)
                    GL.DeleteTexture(texture);

                if (!(GL.GetError() == ErrorCode.NoError))
                    Logger.Log($"Texture size probe: cleanup after {size}x{size} {format} raised a GL error.");
            }
        }

        protected virtual string GetExtensions()
        {
#pragma warning disable CS0618
            GL.GetInteger(All.NumExtensions, out int numExtensions);
#pragma warning restore CS0618

            var extensionsBuilder = new StringBuilder();

            for (int i = 0; i < numExtensions; i++)
                extensionsBuilder.Append($"{GL.GetString(StringNameIndexed.Extensions, i)} ");

            return extensionsBuilder.ToString().TrimEnd();
        }

        protected internal override void BeginFrame(Vector2 windowSize)
        {
            lastBlendingEnabledState = null;
            lastBoundVertexArray = 0;
            boundUniformBuffers.Clear();

            // Seems to be required on some drivers as the context is lost from the draw thread.
            MakeCurrent();

            GL.UseProgram(0);

            base.BeginFrame(windowSize);
        }

        protected internal override void WaitUntilNextFrameReady()
        {
        }

        protected internal override void MakeCurrent() => openGLSurface.MakeCurrent(openGLSurface.WindowContext);
        protected internal override void ClearCurrent() => openGLSurface.ClearCurrent();
        protected internal override void SwapBuffers() => openGLSurface.SwapBuffers();
        protected internal override void WaitUntilIdle() => GL.Finish();

        public bool BindVertexArray(int vaoId)
        {
            if (lastBoundVertexArray == vaoId)
                return false;

            lastBoundVertexArray = vaoId;
            GL.BindVertexArray(vaoId);

            FrameStatistics.Increment(StatisticsCounterType.VBufBinds);
            return true;
        }

        protected override void SetShaderImplementation(IShader shader) => GL.UseProgram((GLShader)shader);

        protected override void SetUniformImplementation<T>(IUniformWithValue<T> uniform)
        {
            switch (uniform)
            {
                case IUniformWithValue<bool> b:
                    GL.Uniform1(uniform.Location, b.GetValue() ? 1 : 0);
                    break;

                case IUniformWithValue<int> i:
                    GL.Uniform1(uniform.Location, i.GetValue());
                    break;

                case IUniformWithValue<float> f:
                    GL.Uniform1(uniform.Location, f.GetValue());
                    break;

                case IUniformWithValue<Vector2> v2:
                    GL.Uniform2(uniform.Location, ref v2.GetValueByRef());
                    break;

                case IUniformWithValue<Vector3> v3:
                    GL.Uniform3(uniform.Location, ref v3.GetValueByRef());
                    break;

                case IUniformWithValue<Vector4> v4:
                    GL.Uniform4(uniform.Location, ref v4.GetValueByRef());
                    break;

                case IUniformWithValue<Matrix2> m2:
                    GL.UniformMatrix2(uniform.Location, false, ref m2.GetValueByRef());
                    break;

                case IUniformWithValue<Matrix3> m3:
                    GL.UniformMatrix3(uniform.Location, false, ref m3.GetValueByRef());
                    break;

                case IUniformWithValue<Matrix4> m4:
                    GL.UniformMatrix4(uniform.Location, false, ref m4.GetValueByRef());
                    break;
            }
        }

        protected override void SetUniformBufferImplementation(string blockName, IUniformBuffer buffer)
            => boundUniformBuffers[blockName] = (IGLUniformBuffer)buffer;

        protected override bool SetTextureImplementation(INativeTexture? texture, int unit)
        {
            if (texture == null)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + unit);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                return true;
            }

            switch (texture)
            {
                case GLVideoTexture glVideo:
                    if (glVideo.TextureIds == null)
                        return false;

                    for (int i = 0; i < glVideo.TextureIds.Length; i++)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0 + unit + i);
                        GL.BindTexture(TextureTarget.Texture2D, glVideo.TextureIds[i]);
                    }

                    break;

                case GLTexture glTexture:
                    if (glTexture.TextureId <= 0)
                        return false;

                    GL.ActiveTexture(TextureUnit.Texture0 + unit);
                    GL.BindTexture(TextureTarget.Texture2D, glTexture.TextureId);
                    break;
            }

            return true;
        }

        protected override void SetFrameBufferImplementation(IFrameBuffer? frameBuffer) =>
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, ((GLFrameBuffer?)frameBuffer)?.FrameBuffer ?? backbufferFramebuffer);

        protected override void DeleteFrameBufferImplementation(IFrameBuffer frameBuffer)
            => ScheduleDisposal(static f => GL.DeleteFramebuffer(f.FrameBuffer), (GLFrameBuffer)frameBuffer);

        protected override void ClearImplementation(ClearInfo clearInfo)
        {
            if (clearInfo.Colour != CurrentClearInfo.Colour)
                GL.ClearColor(clearInfo.Colour);

            if (clearInfo.Depth != CurrentClearInfo.Depth)
            {
                if (IsEmbedded)
                {
                    // GL ES only supports glClearDepthf
                    // See: https://www.khronos.org/registry/OpenGL-Refpages/es3.0/html/glClearDepthf.xhtml
                    GL.ClearDepth((float)clearInfo.Depth);
                }
                else
                {
                    // Older desktop platforms don't support glClearDepthf, so standard GL's double version is used instead
                    // See: https://www.khronos.org/registry/OpenGL-Refpages/gl4/html/glClearDepth.xhtml
                    osuTK.Graphics.OpenGL.GL.ClearDepth(clearInfo.Depth);
                }
            }

            if (clearInfo.Stencil != CurrentClearInfo.Stencil)
                GL.ClearStencil(clearInfo.Stencil);

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        public override void DrawVerticesImplementation(PrimitiveTopology topology, int vertexStart, int verticesCount)
        {
            var glShader = (GLShader)Shader!;
            int currentUniformBinding = 0;
            int currentStorageBinding = 0;

            foreach ((string name, IGLUniformBuffer buffer) in boundUniformBuffers)
            {
                if (glShader.GetUniformBlockIndex(name) is not int index)
                    continue;

                buffer.Flush();

                if (buffer is IGLShaderStorageBufferObject && UseStructuredBuffers)
                {
                    GL4.GL.ShaderStorageBlockBinding(glShader, index, currentStorageBinding);
                    GL4.GL.BindBufferBase(GL4.BufferRangeTarget.ShaderStorageBuffer, currentStorageBinding, buffer.Id);
                    currentStorageBinding++;
                }
                else
                {
                    GL.UniformBlockBinding(glShader, index, currentUniformBinding);
                    GL.BindBufferBase(BufferRangeTarget.UniformBuffer, currentUniformBinding, buffer.Id);
                    currentUniformBinding++;
                }
            }

            GL.DrawElements(GLUtils.ToPrimitiveType(topology), verticesCount, DrawElementsType.UnsignedShort, vertexStart * sizeof(ushort));
        }

        protected override void SetScissorStateImplementation(bool enabled)
        {
            if (enabled)
                GL.Enable(EnableCap.ScissorTest);
            else
                GL.Disable(EnableCap.ScissorTest);
        }

        protected override void SetBlendImplementation(BlendingParameters blendingParameters)
        {
            if (blendingParameters.IsDisabled)
            {
                if (!lastBlendingEnabledState.HasValue || lastBlendingEnabledState.Value)
                    GL.Disable(EnableCap.Blend);

                lastBlendingEnabledState = false;
            }
            else
            {
                if (!lastBlendingEnabledState.HasValue || !lastBlendingEnabledState.Value)
                    GL.Enable(EnableCap.Blend);

                lastBlendingEnabledState = true;

                GL.BlendEquationSeparate(blendingParameters.RGBEquationMode, blendingParameters.AlphaEquationMode);
                GL.BlendFuncSeparate(blendingParameters.SourceBlendingFactor, blendingParameters.DestinationBlendingFactor,
                    blendingParameters.SourceAlphaBlendingFactor, blendingParameters.DestinationAlphaBlendingFactor);
            }
        }

        protected override void SetBlendMaskImplementation(BlendingMask blendingMask)
        {
            GL.ColorMask(blendingMask.HasFlagFast(BlendingMask.Red),
                blendingMask.HasFlagFast(BlendingMask.Green),
                blendingMask.HasFlagFast(BlendingMask.Blue),
                blendingMask.HasFlagFast(BlendingMask.Alpha));
        }

        protected override void SetViewportImplementation(RectangleI viewport) => GL.Viewport(viewport.Left, viewport.Top, viewport.Width, viewport.Height);

        protected override void SetScissorImplementation(RectangleI scissor) => GL.Scissor(scissor.X, Viewport.Height - scissor.Bottom, scissor.Width, scissor.Height);

        protected override void SetDepthInfoImplementation(DepthInfo depthInfo)
        {
            if (depthInfo.DepthTest)
            {
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(GLUtils.ToDepthFunction(depthInfo.Function));
            }
            else
                GL.Disable(EnableCap.DepthTest);

            GL.DepthMask(depthInfo.WriteDepth);
        }

        protected override void SetStencilInfoImplementation(StencilInfo stencilInfo)
        {
            if (stencilInfo.StencilTest)
            {
                GL.Enable(EnableCap.StencilTest);
                GL.StencilFunc(GLUtils.ToStencilFunction(stencilInfo.TestFunction), stencilInfo.TestValue, stencilInfo.Mask);
                GL.StencilOp(
                    GLUtils.ToStencilOperation(stencilInfo.StencilTestFailOperation),
                    GLUtils.ToStencilOperation(stencilInfo.DepthTestFailOperation),
                    GLUtils.ToStencilOperation(stencilInfo.TestPassedOperation));
            }
            else
                GL.Disable(EnableCap.StencilTest);
        }

        protected internal override Image<Rgba32> TakeScreenshot()
        {
            var size = ((IGraphicsSurface)openGLSurface).GetDrawableSize();
            var data = new osu.Framework.Allocation.PooledMemoryOwner<Rgba32>(size.Width * size.Height);

            GL.ReadPixels(0, 0, size.Width, size.Height, PixelFormat.Rgba, PixelType.UnsignedByte, ref MemoryMarshal.GetReference(data.Memory.Span));

            var image = Image.LoadPixelData(data.Memory.Span, size.Width, size.Height);
            image.Mutate(i => i.FlipVertical());
            return image;
        }

        protected internal override Image<Rgba32> ExtractFrameBufferData(IFrameBuffer frameBuffer)
        {
            int width = frameBuffer.Texture.Width;
            int height = frameBuffer.Texture.Height;

            var data = new osu.Framework.Allocation.PooledMemoryOwner<Rgba32>(width * height);

            frameBuffer.Bind();
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, ref MemoryMarshal.GetReference(data.Memory.Span));
            frameBuffer.Unbind();

            var image = Image.LoadPixelData(data.Memory.Span, width, height);

            return image;
        }

        protected override IShaderPart CreateShaderPart(IShaderStore store, string name, byte[]? rawData, ShaderPartType partType)
        {
            ShaderType glType;

            switch (partType)
            {
                case ShaderPartType.Fragment:
                    glType = ShaderType.FragmentShader;
                    break;

                case ShaderPartType.Vertex:
                    glType = ShaderType.VertexShader;
                    break;

                default:
                    throw new ArgumentException($"Unsupported shader part type: {partType}", nameof(partType));
            }

            return new GLShaderPart(this, name, rawData, glType, store);
        }

        protected override IShader CreateShader(string name, IShaderPart[] parts, ShaderCompilationStore compilationStore)
            => new GLShader(this, name, parts.Cast<GLShaderPart>().ToArray(), compilationStore);

        public override IFrameBuffer CreateFrameBuffer(TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float, RenderBufferFormat[]? renderBufferFormats = null, TextureFilteringMode filteringMode = TextureFilteringMode.Linear)
        {
            RenderbufferInternalFormat[]? glFormats = null;

            if (renderBufferFormats != null)
            {
                glFormats = new RenderbufferInternalFormat[renderBufferFormats.Length];

                for (int i = 0; i < renderBufferFormats.Length; i++)
                {
                    switch (renderBufferFormats[i])
                    {
                        case RenderBufferFormat.D16:
                            glFormats[i] = RenderbufferInternalFormat.DepthComponent16;
                            break;

                        case RenderBufferFormat.D32:
                            glFormats[i] = RenderbufferInternalFormat.DepthComponent32f;
                            break;

                        case RenderBufferFormat.D24S8:
                            glFormats[i] = RenderbufferInternalFormat.Depth24Stencil8;
                            break;

                        case RenderBufferFormat.D32S8:
                            glFormats[i] = RenderbufferInternalFormat.Depth32fStencil8;
                            break;

                        default:
                            throw new ArgumentException($"Unsupported render buffer format: {renderBufferFormats[i]}", nameof(renderBufferFormats));
                    }
                }
            }

            return new GLFrameBuffer(this, textureFormat, glFormats, filteringMode);
        }

        protected override IUniformBuffer<TData> CreateUniformBuffer<TData>()
            => new GLUniformBuffer<TData>(this);

        protected override IShaderStorageBufferObject<TData> CreateShaderStorageBufferObject<TData>(int uboSize, int ssboSize)
            => new GLShaderStorageBufferObject<TData>(this, uboSize, ssboSize);

        protected override INativeTexture CreateNativeTexture(int width, int height, bool manualMipmaps = false, TextureFilteringMode filteringMode = TextureFilteringMode.Linear,
                                                              Color4? initialisationColour = null, TexturePixelFormat textureFormat = TexturePixelFormat.R8G8B8A8Float)
            => new GLTexture(this, width, height, textureFormat, manualMipmaps, filteringMode, initialisationColour);

        protected override INativeTexture CreateNativeVideoTexture(int width, int height) => new GLVideoTexture(this, width, height);

        protected override IVertexBatch<TVertex> CreateLinearBatch<TVertex>(int size, int maxBuffers, PrimitiveTopology topology)
            => new GLLinearBatch<TVertex>(this, size, maxBuffers, topology);

        protected override IVertexBatch<TVertex> CreateQuadBatch<TVertex>(int size, int maxBuffers) => new GLQuadBatch<TVertex>(this, size, maxBuffers);
    }
}
