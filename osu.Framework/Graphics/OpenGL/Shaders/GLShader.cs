// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Threading;
using osuTK.Graphics.ES30;
using Veldrid;
using Veldrid.SPIRV;
using static osu.Framework.Threading.ScheduledDelegate;
using GL4 = osuTK.Graphics.OpenGL;
using ProgramInterface = osuTK.Graphics.OpenGL.ProgramInterface;

namespace osu.Framework.Graphics.OpenGL.Shaders
{
    internal class GLShader : IShader, IPerVertexMaskingShader
    {
        private readonly GLRenderer renderer;
        private readonly string name;
        private readonly GLShaderPart[] parts;

        private readonly ScheduledDelegate shaderCompileDelegate;
        private readonly ShaderCompilationStore compilationStore;

        /// <summary>
        /// The number of programs restored from cached driver binaries, for diagnostics and tests.
        /// </summary>
        internal static int ProgramBinaryLoads;

        private readonly Dictionary<string, int> uniformBlocks = new Dictionary<string, int>();
        private readonly List<Uniform<int>> textureUniforms = new List<Uniform<int>>();

        public bool IsLoaded { get; private set; }

        public bool IsBound { get; private set; }

        public bool UsesPerVertexMasking { get; }

        private int programID = -1;

        private readonly GLShaderPart vertexPart;
        private readonly GLShaderPart fragmentPart;
        private readonly VertexFragmentShaderCompilation compilation;

        internal GLShader(GLRenderer renderer, string name, GLShaderPart[] parts, ShaderCompilationStore compilationStore)
        {
            this.renderer = renderer;
            this.name = name;
            this.parts = parts;
            this.compilationStore = compilationStore;

            vertexPart = parts.Single(p => p.Type == ShaderType.VertexShader);
            fragmentPart = parts.Single(p => p.Type == ShaderType.FragmentShader);

            // This part of the compilation is quite CPU expensive.
            // Running it in the constructor will ensure that BDL usages can correctly offload this as an async operation.
            try
            {
                // Shaders are in "Vulkan GLSL" format. They need to be cross-compiled to GLSL.
                string vertexText = vertexPart.GetRawText();
                string fragmentText = fragmentPart.GetRawText();

                UsesPerVertexMasking = PerVertexMasking.IsSupported(vertexText, fragmentText);

                if (UsesPerVertexMasking)
                {
                    vertexText = PerVertexMasking.AddDefine(vertexText);
                    fragmentText = PerVertexMasking.AddDefine(fragmentText);
                }

                if (TextureSlots.IsSupported(vertexText))
                    fragmentText = PerVertexMasking.AddDefine(fragmentText, TextureSlots.DEFINE);

                compilation = compilationStore.CompileVertexFragment(
                    vertexText,
                    fragmentText,
                    renderer.IsEmbedded ? CrossCompileTarget.ESSL : CrossCompileTarget.GLSL);
            }
            catch (Exception e)
            {
                throw new ProgramLinkingFailedException(name, e.ToString());
            }

            // Final GPU level compilation needs to be run on the draw thread.
            renderer.ScheduleExpensiveOperation(shaderCompileDelegate = new ScheduledDelegate(compile));
        }

        private void compile()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (IsLoaded)
                throw new InvalidOperationException("Attempting to compile an already-compiled shader.");

            if (parts.Length == 0)
                return;

            programID = CreateProgram();

            if (!CompileInternal())
                throw new ProgramLinkingFailedException(name, GetProgramLog());

            IsLoaded = true;
        }

        internal void EnsureShaderCompiled()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (shaderCompileDelegate.State == RunState.Waiting)
                shaderCompileDelegate.RunTask();
        }

        public void Bind()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            if (IsBound)
                return;

            EnsureShaderCompiled();

            renderer.BindShader(this);

            for (int i = 0; i < textureUniforms.Count; i++)
                textureUniforms[i].Update();

            IsBound = true;
        }

        public void Unbind()
        {
            if (!IsBound)
                return;

            renderer.UnbindShader(this);

            IsBound = false;
        }

        public int? GetUniformBlockIndex(string name) => uniformBlocks.TryGetValue(name, out int index) ? index : null;

        public virtual void BindUniformBlock(string blockName, IUniformBuffer buffer)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            EnsureShaderCompiled();

            renderer.BindUniformBuffer(blockName, buffer);
        }

        private protected virtual bool CompileInternal()
        {
            string? binaryKey = renderer.GetProgramBinaryKey(compilation.VertexText, compilation.FragmentText);

            if (binaryKey == null || !tryLoadProgramBinary(binaryKey))
            {
                // Parts are shared between programs by name, but the defines added in the constructor depend on each program's vertex shader.
                // A stage compiled once for every program would carry the first program's interface, which GLES linkers reject for the others.
                int vertexShader = compileShader(vertexPart, compilation.VertexText);
                int fragmentShader;

                try
                {
                    fragmentShader = compileShader(fragmentPart, compilation.FragmentText);
                }
                catch
                {
                    GL.DeleteShader(vertexShader);
                    throw;
                }

                GL.AttachShader(this, vertexShader);
                GL.AttachShader(this, fragmentShader);

                if (binaryKey != null)
                    GL.ProgramParameter(this, ProgramParameterName.ProgramBinaryRetrievableHint, 1);

                GL.LinkProgram(this);
                GL.GetProgram(this, GetProgramParameterName.LinkStatus, out int linkResult);

                // The linked program keeps its own code.
                GL.DetachShader(this, vertexShader);
                GL.DetachShader(this, fragmentShader);
                GL.DeleteShader(vertexShader);
                GL.DeleteShader(fragmentShader);

                if (linkResult != 1)
                    return false;

                if (binaryKey != null)
                    saveProgramBinary(binaryKey);
            }

            int textureIndex = 0;

            foreach (ResourceLayoutDescription layout in compilation.Reflection.ResourceLayouts)
            {
                if (layout.Elements.Length == 0)
                    continue;

                if (layout.Elements.Any(e => e.Kind == ResourceKind.TextureReadOnly || e.Kind == ResourceKind.TextureReadWrite))
                {
                    ResourceLayoutElementDescription textureElement = layout.Elements.First(e => e.Kind == ResourceKind.TextureReadOnly || e.Kind == ResourceKind.TextureReadWrite);

                    if (layout.Elements.All(e => e.Kind != ResourceKind.Sampler))
                        throw new ProgramLinkingFailedException(name, $"Texture {textureElement.Name} has no associated sampler.");

                    textureUniforms.Add(new Uniform<int>(renderer, this, textureElement.Name, GL.GetUniformLocation(this, textureElement.Name))
                    {
                        Value = textureIndex++
                    });
                }
                else
                {
                    int blockIndex = -1;

                    switch (layout.Elements[0].Kind)
                    {
                        case ResourceKind.UniformBuffer:
                            blockIndex = GL.GetUniformBlockIndex(this, layout.Elements[0].Name);
                            break;

                        case ResourceKind.StructuredBufferReadOnly:
                        case ResourceKind.StructuredBufferReadWrite:
                            blockIndex = GL4.GL.GetProgramResourceIndex(this, ProgramInterface.ShaderStorageBlock, layout.Elements[0].Name);
                            break;
                    }

                    // Blocks the driver found unused report an invalid index (e.g. the masking buffer in programs that do not mask).
                    if (blockIndex >= 0)
                        uniformBlocks[layout.Elements[0].Name] = blockIndex;
                }
            }

            return true;
        }

        /// <summary>
        /// Compiles one stage of this program from its final text.
        /// </summary>
        /// <returns>The shader object, which the caller deletes.</returns>
        private static int compileShader(GLShaderPart part, string text)
        {
            int shader = GL.CreateShader(part.Type);

            GL.ShaderSource(shader, text);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compileResult);

            if (compileResult == 1)
                return shader;

            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new PartCompilationFailedException(part.Name, log);
        }

        /// <summary>
        /// GL_PROGRAM_BINARY_LENGTH, which is missing from the bindings' enum.
        /// </summary>
        private const GetProgramParameterName program_binary_length = (GetProgramParameterName)0x8741;

        /// <summary>
        /// Restores the program from a cached driver binary, skipping compilation and linking.
        /// Drivers may reject binaries (e.g. after an update), in which case the program is compiled normally.
        /// </summary>
        private unsafe bool tryLoadProgramBinary(string key)
        {
            if (!compilationStore.TryGetCachedBinary(key, out byte[]? cached) || cached.Length <= sizeof(int))
                return false;

            int format = BitConverter.ToInt32(cached, 0);

            fixed (byte* data = &cached[sizeof(int)])
                GL.ProgramBinary(this, (All)format, (IntPtr)data, cached.Length - sizeof(int));

            GL.GetProgram(this, GetProgramParameterName.LinkStatus, out int linkResult);

            // Rejected binaries may or may not raise an error depending on the driver; clear it either way.
            GL.GetError();

            if (linkResult != 1)
                return false;

            Interlocked.Increment(ref ProgramBinaryLoads);
            return true;
        }

        private unsafe void saveProgramBinary(string key)
        {
            GL.GetProgram(this, program_binary_length, out int length);

            if (length <= 0)
                return;

            byte[] cached = new byte[sizeof(int) + length];
            int written;
            All format;

            fixed (byte* data = &cached[sizeof(int)])
                GL.GetProgramBinary(this, length, &written, &format, (IntPtr)data);

            if (GL.GetError() != ErrorCode.NoError || written <= 0)
                return;

            BitConverter.TryWriteBytes(cached.AsSpan(0, sizeof(int)), (int)format);
            compilationStore.SaveBinary(key, cached.AsSpan(0, sizeof(int) + written).ToArray());
        }

        private protected virtual string GetProgramLog() => GL.GetProgramInfoLog(this);

        private protected virtual int CreateProgram() => GL.CreateProgram();

        private protected virtual void DeleteProgram(int id) => GL.DeleteProgram(id);

        public override string ToString() => $@"{name} Shader (Compiled: {programID != -1})";

        public static implicit operator int(GLShader shader) => shader.programID;

        #region IDisposable Support

        protected internal bool IsDisposed { get; private set; }

        ~GLShader()
        {
            renderer.ScheduleDisposal(static s => s.Dispose(false), this);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            IsDisposed = true;

            if (shaderCompileDelegate.IsNotNull())
                shaderCompileDelegate.Cancel();

            if (programID != -1)
                DeleteProgram(this);
        }

        #endregion

        public class PartCompilationFailedException : Exception
        {
            public PartCompilationFailedException(string partName, string log)
                : base($"A {typeof(GLShaderPart)} failed to compile: {partName}:\n{log.Trim()}")
            {
            }
        }

        public class ProgramLinkingFailedException : Exception
        {
            public ProgramLinkingFailedException(string programName, string log)
                : base($"A {typeof(GLShader)} failed to link: {programName}:\n{log.Trim()}")
            {
            }
        }
    }
}
