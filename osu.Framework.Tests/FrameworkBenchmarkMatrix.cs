// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Testing.Benchmarking;
using osu.Framework.Tests.Visual.Performance;
using osu.Framework.Tests.Visual.UserInterface;

namespace osu.Framework.Tests
{
    /// <summary>
    /// The framework's visual tests as benchmarks, against the renderer optimisations that can be switched at startup.
    /// Run with <c>--benchmark-matrix</c> (see <see cref="BenchmarkProgram"/>).
    /// </summary>
    internal static class FrameworkBenchmarkMatrix
    {
        public static BenchmarkMatrix Create() => new BenchmarkMatrix(@"framework", cases, factors);

        private static readonly BenchmarkCase[] cases =
        {
            new BenchmarkCase(@"Boxes", typeof(TestSceneBoxPerformance))
            {
                Description = "4,000 boxes, each its own draw node",
                Sliders = new Dictionary<string, double> { [@"count"] = 4000 },
            },
            new BenchmarkCase(@"Rounded masking", typeof(TestSceneMaskingPerformance))
            {
                Description = "2,000 masking containers with rounded corners",
                Sliders = new Dictionary<string, double> { [@"count"] = 2000, [@"corner radius"] = 8 },
            },
            new BenchmarkCase(@"Glow edge effects", typeof(TestSceneMaskingPerformance))
            {
                Description = "1,000 rounded containers with glowing edges",
                Steps = new[] { @"glow edge effect" },
                Sliders = new Dictionary<string, double> { [@"count"] = 1000, [@"corner radius"] = 8, [@"edge effect radius"] = 12 },
            },
            new BenchmarkCase(@"Unique textures", typeof(TestSceneTexturePerformance))
            {
                Description = "500 sprites, each with its own texture",
                Steps = new[] { @"unique textures" },
                Sliders = new Dictionary<string, double> { [@"count"] = 500 },
            },
            new BenchmarkCase(@"Buffered containers", typeof(TestSceneBufferingPerformance))
            {
                Description = "500 sprites in framebuffers redrawn every frame",
                Sliders = new Dictionary<string, double> { [@"count"] = 500 },
            },
            new BenchmarkCase(@"Sprite text", typeof(TestSceneSpriteTextPerformance))
            {
                Description = "Four paragraphs of 500 random words",
                Sliders = new Dictionary<string, double> { [@"words count"] = 500, [@"paragraphs count"] = 4 },
            },
            new BenchmarkCase(@"Progress rings", typeof(TestSceneCircularProgressRingsPerformance))
            {
                Description = "Circular progress rings at the scene's defaults",
            },
            new BenchmarkCase(@"Self-overlapping path", typeof(TestSceneDrawablePath))
            {
                Description = "A smooth path crossing itself, from the path visual test",
                Test = nameof(TestSceneDrawablePath.TestSelfOverlappingPath),
            },
        };

        private static readonly BenchmarkFactor[] factors =
        {
            new BenchmarkFactor(@"Per-vertex masking")
            {
                Description = @"PerVertexMasking setting",
                EnabledByDefault = true,
                DisabledSettings = new Dictionary<string, string> { [@"PerVertexMasking"] = @"false" },
            },
            new BenchmarkFactor(@"Texture slots")
            {
                Description = @"TextureSlots setting",
                EnabledByDefault = true,
                DisabledSettings = new Dictionary<string, string> { [@"TextureSlots"] = @"false" },
            },
            new BenchmarkFactor(@"Direct paths")
            {
                Description = @"DirectPathRendering setting",
                EnabledSettings = new Dictionary<string, string> { [@"DirectPathRendering"] = @"true" },
            },
            new BenchmarkFactor(@"Shared atlases")
            {
                Description = @"SharedTextureAtlases setting",
                EnabledSettings = new Dictionary<string, string> { [@"SharedTextureAtlases"] = @"true" },
                IncludedByDefault = false,
            },
            new BenchmarkFactor(@"Single-channel fonts")
            {
                Description = @"SingleChannelFontAtlas setting",
                EnabledByDefault = true,
                // Without the second setting, the configuration's migration turns single-channel atlases back on.
                DisabledSettings = new Dictionary<string, string> { [@"SingleChannelFontAtlas"] = @"false", [@"SingleChannelFontAtlasDefaulted"] = @"true" },
                IncludedByDefault = false,
            },
            new BenchmarkFactor(@"Vulkan")
            {
                Description = @"OfflineVulkanRenderer setting",
                EnabledSettings = new Dictionary<string, string> { [@"OfflineVulkanRenderer"] = @"true" },
                IncludedByDefault = false,
            },
        };
    }
}
