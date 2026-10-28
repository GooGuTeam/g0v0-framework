// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.OpenGL
{
    /// <summary>Runs only on the native GL renderer's current context, before game resources load.</summary>
    internal static class GLBindlessTextureProbe
    {
        public static BindlessTextureProbeResult Run(IOpenGLGraphicsSurface surface, string extensions)
        {
            if (!extensions.Split(' ').Contains("GL_ARB_bindless_texture"))
                return new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unsupported, "GL_ARB_bindless_texture is not advertised.");

            var getHandle = load<GetTextureHandle>(surface, "glGetTextureHandleARB");
            var makeResident = load<SetResidency>(surface, "glMakeTextureHandleResidentARB");
            var makeNonResident = load<SetResidency>(surface, "glMakeTextureHandleNonResidentARB");
            var isResident = load<IsResident>(surface, "glIsTextureHandleResidentARB");
            var setUniform = load<UniformHandle>(surface, "glUniformHandleui64ARB");
            int texture = 0;
            int vertex = 0;
            int fragment = 0;
            int program = 0;
            ulong handle = 0;
            bool resident = false;
            int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
            int previousProgram = GL.GetInteger(GetPName.CurrentProgram);
            try
            {
                vertex = compile(ShaderType.VertexShader, "#version 400\nvoid main() { gl_Position = vec4(0, 0, 0, 1); }");
                fragment = compile(ShaderType.FragmentShader,
                    "#version 400\n#extension GL_ARB_bindless_texture : require\nlayout(bindless_sampler) uniform sampler2D probeTexture;\nout vec4 colour;\nvoid main() { colour = texture(probeTexture, vec2(0.5)); }");
                program = GL.CreateProgram();
                GL.AttachShader(program, vertex);
                GL.AttachShader(program, fragment);
                GL.LinkProgram(program);
                GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
                if (linked == 0)
                    throw new InvalidOperationException(GL.GetProgramInfoLog(program));

                texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexImage2D(TextureTarget2d.Texture2D, 0, TextureComponentCount.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
                handle = getHandle((uint)texture);
                if (handle == 0)
                    throw new InvalidOperationException("Driver returned a zero bindless texture handle.");
                makeResident(handle);
                resident = isResident(handle) != 0;
                if (!resident)
                    throw new InvalidOperationException("Texture handle could not be made resident.");
                GL.UseProgram(program);
                int location = GL.GetUniformLocation(program, "probeTexture");
                if (location < 0)
                    throw new InvalidOperationException("Bindless sampler uniform is unavailable.");
                setUniform(location, handle);
                var error = GL.GetError();
                if (error != ErrorCode.NoError)
                    throw new InvalidOperationException($"Bindless probe GL error: {error}");
                return new BindlessTextureProbeResult(BindlessTextureProbeStatus.ShaderAndResidencyValidated,
                    "ARB shader compile/link, texture residency and sampler handle assignment passed. Sampling/readback and non-uniform indexing are not validated; renderer remains bound-texture based.");
            }
            finally
            {
                GL.UseProgram(previousProgram);
                if (resident)
                    makeNonResident(handle);
                GL.BindTexture(TextureTarget.Texture2D, previousTexture);
                if (texture != 0) GL.DeleteTexture(texture);
                if (program != 0) GL.DeleteProgram(program);
                if (vertex != 0) GL.DeleteShader(vertex);
                if (fragment != 0) GL.DeleteShader(fragment);
            }
        }

        private static int compile(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            try
            {
                GL.ShaderSource(shader, source);
                GL.CompileShader(shader);
                GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
                if (compiled == 0)
                    throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
                return shader;
            }
            catch
            {
                GL.DeleteShader(shader);
                throw;
            }
        }

        private static T load<T>(IOpenGLGraphicsSurface surface, string name) where T : Delegate
        {
            IntPtr address = surface.GetProcAddress(name);
            if (address == IntPtr.Zero || address == new IntPtr(1) || address == new IntPtr(2) || address == new IntPtr(3) || address == new IntPtr(-1))
                throw new NotSupportedException($"Missing bindless entry point: {name}");
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate ulong GetTextureHandle(uint texture);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void SetResidency(ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate byte IsResident(ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void UniformHandle(int location, ulong handle);
    }
}
