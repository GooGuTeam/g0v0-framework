// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;
using Veldrid;
using SharpGen.Runtime;
using Veldrid.SPIRV;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using static osu.Framework.Threading.ScheduledDelegate;

namespace osu.Framework.Graphics.Veldrid.Shaders
{
    internal class VeldridShader : IShader, IPerVertexMaskingShader
    {
        private readonly string name;
        private readonly VeldridShaderPart[] parts;
        private readonly ShaderCompilationStore compilationStore;
        private readonly IVeldridRenderer renderer;

        public Shader[]? Shaders;

        private readonly ScheduledDelegate shaderInitialiseDelegate;

        public bool IsLoaded => Shaders != null;

        public bool IsBound { get; private set; }

        public bool UsesPerVertexMasking { get; private set; }

        private ShaderDescription vertexShaderDescription;
        private ShaderDescription fragmentShaderDescription;

        public int LayoutCount => uniformLayouts.Count + textureLayouts.Count;

        private readonly Dictionary<string, VeldridUniformLayout> uniformLayouts = new Dictionary<string, VeldridUniformLayout>();
        private readonly List<VeldridUniformLayout> textureLayouts = new List<VeldridUniformLayout>();

        public VeldridShader(IVeldridRenderer renderer, string name, VeldridShaderPart[] parts, ShaderCompilationStore compilationStore)
        {
            this.name = name;
            this.parts = parts;
            this.compilationStore = compilationStore;
            this.renderer = renderer;

            // This part of the compilation is quite CPU expensive.
            // Running it in the constructor will ensure that BDL usages can correctly offload this as an async operation.
            compile();

            // Final GPU level load/compilation needs to be run on the draw thread.
            renderer.ScheduleExpensiveOperation(shaderInitialiseDelegate = new ScheduledDelegate(loadToGpu));
        }

        internal void EnsureShaderInitialised()
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (shaderInitialiseDelegate.State == RunState.Waiting)
                shaderInitialiseDelegate.RunTask();
        }

        public void Bind()
        {
            if (IsBound)
                return;

            EnsureShaderInitialised();

            renderer.BindShader(this);

            IsBound = true;
        }

        public void Unbind()
        {
            if (!IsBound)
                return;

            renderer.UnbindShader(this);
            IsBound = false;
        }

        public void BindUniformBlock(string blockName, IUniformBuffer buffer)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            EnsureShaderInitialised();

            renderer.BindUniformBuffer(blockName, buffer);
        }

        public VeldridUniformLayout? GetTextureLayout(int textureUnit) => textureUnit >= textureLayouts.Count ? null : textureLayouts[textureUnit];

        public VeldridUniformLayout? GetUniformBufferLayout(string name) => uniformLayouts.GetValueOrDefault(name);

        private void compile()
        {
            Debug.Assert(parts.Length == 2);

            VeldridShaderPart vertex = parts.Single(p => p.Type == ShaderPartType.Vertex);
            VeldridShaderPart fragment = parts.Single(p => p.Type == ShaderPartType.Fragment);

            // some attributes from the vertex output may not be used by the fragment shader, but that could break some renderers (e.g. D3D11).
            // therefore include any unused vertex output to a fragment shader as fragment input & output.
            // Vulkan permits unused vertex outputs. Synthesising fragment outputs only adds unused
            // colour attachments to the shader interface and triggers validation warnings.
            string fragmentSource = fragment.GetRawText();
            UsesPerVertexMasking = PerVertexMasking.IsSupported(vertex.GetRawText(), fragmentSource);

            // Without the opt-in definition, the per-vertex masking inputs in sh_Masking.h are preprocessed away.
            int[]? undeclaredInputs = !UsesPerVertexMasking && fragmentSource.Contains("v_MaskingIndex", StringComparison.Ordinal)
                ? PerVertexMasking.FRAGMENT_INPUT_LOCATIONS
                : null;

            // Vulkan only receives the inputs: Qualcomm's driver (Adreno 630) fails to link stages whose vertex outputs the fragment shader does not declare.
            fragment = fragment.WithPassthroughInput(vertex.Outputs, undeclaredInputs, declareOutputs: renderer.SurfaceType != GraphicsSurfaceType.Vulkan);

            string vertexText = vertex.GetRawText();
            string fragmentText = fragment.GetRawText();

            if (UsesPerVertexMasking)
            {
                vertexText = PerVertexMasking.AddDefine(vertexText);
                fragmentText = PerVertexMasking.AddDefine(fragmentText);
            }

            bool bindless = renderer.UsesBindlessTextures && TextureSlots.IsSupported(vertexText);

            if (bindless)
            {
                vertexText = VeldridBindlessTextures.AddVertexDefines(vertexText);
                fragmentText = VeldridBindlessTextures.AddFragmentDefines(fragmentText);
            }
            else if (renderer is Renderer { UsesTextureSlotShaders: true } && TextureSlots.IsSupported(vertexText))
                fragmentText = PerVertexMasking.AddDefine(fragmentText, TextureSlots.DEFINE);

            if (renderer.SurfaceType == GraphicsSurfaceType.Vulkan)
            {
                vertexText = matchInterfacePrecision(vertexText);
                fragmentText = matchInterfacePrecision(fragmentText);
            }

            try
            {
                bool cached = true;

                vertexShaderDescription = new ShaderDescription(
                    ShaderStages.Vertex,
                    Array.Empty<byte>(),
                    renderer.Factory.BackendType == GraphicsBackend.Metal ? "main0" : "main");

                fragmentShaderDescription = new ShaderDescription(
                    ShaderStages.Fragment,
                    Array.Empty<byte>(),
                    renderer.Factory.BackendType == GraphicsBackend.Metal ? "main0" : "main");

                // GLSL cross compile is always performed for reflection, even though the cross-compiled shaders aren't used under other backends.
                VertexFragmentShaderCompilation compilation = compilationStore.CompileVertexFragment(
                    vertexText,
                    fragmentText,
                    RuntimeInfo.IsMobile ? CrossCompileTarget.ESSL : CrossCompileTarget.GLSL);

                cached &= compilation.WasCached;

                if (renderer.SurfaceType == GraphicsSurfaceType.Vulkan)
                {
                    vertexShaderDescription.ShaderBytes = compilation.VertexBytes;
                    fragmentShaderDescription.ShaderBytes = compilation.FragmentBytes;
                }
                else
                {
                    VertexFragmentShaderCompilation platformCompilation = compilation;

                    // If we don't have an OpenGL surface, we need to cross-compile once more for the correct platform.
                    if (renderer.SurfaceType != GraphicsSurfaceType.OpenGL)
                    {
                        CrossCompileTarget target = renderer.SurfaceType switch
                        {
                            GraphicsSurfaceType.Metal => CrossCompileTarget.MSL,
                            GraphicsSurfaceType.Direct3D11 => CrossCompileTarget.HLSL,
                            _ => throw new InvalidOperationException($"Unsupported surface type: {renderer.SurfaceType}.")
                        };

                        platformCompilation = compilationStore.CompileVertexFragment(
                            vertexText,
                            fragmentText,
                            target);

                        cached &= platformCompilation.WasCached;
                    }

                    if (renderer.SurfaceType == GraphicsSurfaceType.Direct3D11)
                    {
                        // Compile HLSL to bytecode here (off the draw thread when loaded asynchronously) and persist it,
                        // so that GPU initialisation does not run D3DCompile.
                        string profileSuffix = getDirect3DProfileSuffix();
                        vertexShaderDescription.ShaderBytes = compilationStore.CompileHlslBytecode(platformCompilation.VertexText, vertexShaderDescription.EntryPoint, $"vs_{profileSuffix}");
                        fragmentShaderDescription.ShaderBytes = compilationStore.CompileHlslBytecode(platformCompilation.FragmentText, fragmentShaderDescription.EntryPoint, $"ps_{profileSuffix}");
                    }
                    else
                    {
                        vertexShaderDescription.ShaderBytes = Encoding.UTF8.GetBytes(platformCompilation.VertexText);
                        fragmentShaderDescription.ShaderBytes = Encoding.UTF8.GetBytes(platformCompilation.FragmentText);
                    }
                }

                if (bindless)
                    VeldridBindlessTextures.SetArrayLength(compilation.Reflection.ResourceLayouts);

                for (int set = 0; set < compilation.Reflection.ResourceLayouts.Length; set++)
                {
                    ResourceLayoutDescription layout = compilation.Reflection.ResourceLayouts[set];

                    if (layout.Elements.Length == 0)
                        continue;

                    if (layout.Elements.Any(e => e.Kind == ResourceKind.TextureReadOnly || e.Kind == ResourceKind.TextureReadWrite))
                    {
                        ResourceLayoutElementDescription textureElement = layout.Elements.First(e => e.Kind == ResourceKind.TextureReadOnly || e.Kind == ResourceKind.TextureReadWrite);

                        if (layout.Elements.All(e => e.Kind != ResourceKind.Sampler))
                            throw new InvalidOperationException($"Texture {textureElement.Name} has no associated sampler.");

                        textureLayouts.Add(new VeldridUniformLayout(set, renderer.Factory.CreateResourceLayout(layout)));
                    }
                    else
                    {
                        layout.Elements[0].Options |= ResourceLayoutElementOptions.DynamicBinding;

                        switch (layout.Elements[0].Kind)
                        {
                            case ResourceKind.UniformBuffer:
                            case ResourceKind.StructuredBufferReadOnly:
                            case ResourceKind.StructuredBufferReadWrite:
                                uniformLayouts[layout.Elements[0].Name] = new VeldridUniformLayout(set, renderer.Factory.CreateResourceLayout(layout));
                                break;
                        }
                    }
                }

                Logger.Log(cached
                    ? $"🖍️ Shader {name} loaded from cache!"
                    : $"🖍️ Shader {name} compiled!");
            }
            catch (Exception e)
            {
                Logger.Error(e, $"🖍️ Failed to initialise shader {name}");
                throw;
            }
        }

        /// <summary>
        /// The shader model Veldrid's D3D11 backend selects for the device: 5.0 at feature level 11.0 and above, 4.0 below.
        /// </summary>
        private string getDirect3DProfileSuffix() => direct3d_profile_suffixes.GetValue(renderer.Device, static device =>
        {
            // The wrapper does not own a reference to the device, so it must not be disposed.
            var d3dDevice = MarshallingHelpers.FromPointer<ID3D11Device>(device.GetD3D11Info().Device).AsNonNull();
            return d3dDevice.FeatureLevel >= FeatureLevel.Level_11_0 ? "5_0" : "4_0";
        });

        /// <summary>
        /// Matches a located stage input or output declaration, up to its type, including any precision qualifier.
        /// </summary>
        private static readonly Regex interface_precision = new Regex(@"(layout\s*\([^)]*\blocation\s*=\s*\d+[^)]*\)\s*(?:(?:flat|smooth|noperspective)\s+)?(?:in|out)\s+)(?:(?:lowp|mediump|highp)\s+)?",
            RegexOptions.Compiled);

        /// <summary>
        /// Declares every stage input and output highp. The shaders declare some varyings with different precisions in each stage (e.g. a highp
        /// vertex output read as mediump, or left to a mediump default precision), which SPIR-V carries as differing RelaxedPrecision decorations.
        /// Qualcomm's Vulkan driver (Adreno 630) refuses to link such stages, so give both sides the same full precision.
        /// </summary>
        private static string matchInterfacePrecision(string text) => interface_precision.Replace(text, "$1highp ");

        private static readonly ConditionalWeakTable<GraphicsDevice, string> direct3d_profile_suffixes = new ConditionalWeakTable<GraphicsDevice, string>();

        private void loadToGpu()
        {
            Shaders = new[]
            {
                renderer.Factory.CreateShader(vertexShaderDescription),
                renderer.Factory.CreateShader(fragmentShaderDescription)
            };
        }

        private bool isDisposed;

        ~VeldridShader()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            renderer.ScheduleDisposal(static s =>
            {
                if (s.Shaders != null)
                {
                    for (int i = 0; i < s.Shaders.Length; i++)
                        s.Shaders[i].Dispose();
                }

                foreach (var (_, layout) in s.uniformLayouts)
                    layout.Dispose();

                foreach (var layout in s.textureLayouts)
                    layout.Dispose();

                s.uniformLayouts.Clear();
                s.textureLayouts.Clear();
                s.Shaders = null;
            }, this);
        }
    }
}
