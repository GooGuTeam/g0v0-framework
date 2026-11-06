// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Configuration;
using osu.Framework.Graphics.Shaders;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Bindless texture slots on Vulkan: the default texture shader samples a table of <see cref="TABLE_SIZE"/> textures with a per-primitive,
    /// non-uniform index (<c>VK_EXT_descriptor_indexing</c>), instead of four separately bound slots.
    /// </summary>
    /// <remarks>
    /// Experimental: enabled through <see cref="FrameworkSetting.VulkanBindlessTextures"/> at startup. It requests descriptor indexing from the device, and is used only when
    /// the device enables it. Textures in the table share a linear and a nearest sampler; each texture's view limits it to the mip levels
    /// its own sampler would use.
    /// </remarks>
    internal static class VeldridBindlessTextures
    {
        public const int TABLE_SIZE = 16;

        public const string DEFINE = "G0V0_BINDLESS_TEXTURES";

        private const string array_name = "m_Textures";

        public static bool Requested => FrameworkFeatureFlags.VulkanBindlessTextures;

        /// <summary>
        /// Whether a device can sample the table with non-uniform indices.
        /// </summary>
        public static bool IsSupported(GraphicsDevice device)
        {
            if (!Requested || device.BackendType != GraphicsBackend.Vulkan)
                return false;

            var info = device.GetVulkanInfo();
            return info.TextureDescriptorIndexingEnabled && info.SampledTextureArrayLimit >= TABLE_SIZE;
        }

        public static string AddVertexDefines(string text) => PerVertexMasking.AddDefine(text, $"G0V0_TEXTURE_SLOT_COUNT {TABLE_SIZE}.0");

        public static string AddFragmentDefines(string text)
            => PerVertexMasking.AddDefine(text, $"{DEFINE}\n#extension GL_EXT_nonuniform_qualifier : require");

        /// <summary>
        /// Sets the array length of the table in reflected layouts, since reflection does not report it.
        /// </summary>
        public static void SetArrayLength(ResourceLayoutDescription[] layouts)
        {
            foreach (var layout in layouts)
            {
                for (int i = 0; i < layout.Elements.Length; i++)
                {
                    if (layout.Elements[i].Name == array_name)
                        layout.Elements[i].ArrayLength = TABLE_SIZE;
                }
            }
        }

        public static bool Declares(ResourceLayoutDescription layout) => layout.Elements.Any(e => e.Name == array_name);
    }
}
