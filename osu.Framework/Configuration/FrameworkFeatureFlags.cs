// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Configuration
{
    /// <summary>
    /// Startup values for experimental features. Saved settings take effect on the next host run.
    /// </summary>
    internal static class FrameworkFeatureFlags
    {
        public static bool DirectPathRendering { get; private set; }

        public static bool VulkanBindlessTextures { get; private set; }

        public static bool VulkanDescriptorIndexing { get; private set; }

        public static bool VulkanValidation { get; private set; }

        public static bool VulkanCaptureInterop { get; private set; }

        public static bool UseMediaFoundationVideoDecoder { get; private set; } = true;

        public static bool OfflineVulkanRenderer { get; private set; }

        private static AtlasPackingStrategy fontAtlasPacking = AtlasPackingStrategy.Etagere;
        private static AtlasPackingStrategy textureAtlasPacking = AtlasPackingStrategy.Guillotiere;
        private static bool lowResolutionFonts;
        private static bool singleChannelFontAtlas = true;
        private static bool showOverdrawIndicator;
        private static bool probeBindlessTextures = true;

        public static void Apply(FrameworkConfigManager config)
        {
            DirectPathRendering = config.Get<bool>(FrameworkSetting.DirectPathRendering);
            VulkanBindlessTextures = config.Get<bool>(FrameworkSetting.VulkanBindlessTextures);
            VulkanDescriptorIndexing = config.Get<bool>(FrameworkSetting.VulkanDescriptorIndexing);
            VulkanValidation = config.Get<bool>(FrameworkSetting.VulkanValidation);
            VulkanCaptureInterop = config.Get<bool>(FrameworkSetting.VulkanCaptureInterop);
            UseMediaFoundationVideoDecoder = config.Get<bool>(FrameworkSetting.UseMediaFoundationVideoDecoder);
            OfflineVulkanRenderer = config.Get<bool>(FrameworkSetting.OfflineVulkanRenderer);
            PerVertexMasking.Enabled = config.Get<bool>(FrameworkSetting.PerVertexMasking);
            Renderer.TextureSlotsEnabled = config.Get<bool>(FrameworkSetting.TextureSlots);
            TextureStore.ShareAtlases = config.Get<bool>(FrameworkSetting.SharedTextureAtlases);
            fontAtlasPacking = config.Get<AtlasPackingStrategy>(FrameworkSetting.FontAtlasPacking);
            textureAtlasPacking = config.Get<AtlasPackingStrategy>(FrameworkSetting.TextureAtlasPacking);
            lowResolutionFonts = config.Get<bool>(FrameworkSetting.LowResolutionFonts);
            singleChannelFontAtlas = config.Get<bool>(FrameworkSetting.SingleChannelFontAtlas);
            showOverdrawIndicator = config.Get<bool>(FrameworkSetting.ShowOverdrawIndicator);
            probeBindlessTextures = config.Get<bool>(FrameworkSetting.ProbeBindlessTextures);
        }

        public static void ApplyTo(Renderer renderer)
        {
            renderer.FontAtlasPacking = fontAtlasPacking;
            renderer.TextureAtlasPacking = textureAtlasPacking;
            renderer.LowResolutionFonts = lowResolutionFonts;
            renderer.SingleChannelFontAtlas = singleChannelFontAtlas;
            renderer.ShowOverdrawIndicator = showOverdrawIndicator;
            renderer.ProbeBindlessTextures = probeBindlessTextures;
        }
    }
}
