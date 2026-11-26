// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Text;

namespace osu.Framework.IO.Stores
{
    public class FontStore : TextureStore, ITexturedGlyphLookupStore
    {
        // High-resolution glyphs otherwise split short text runs across many small sprite atlases.
        // Larger pages trade initialisation and whole-page mipmap costs for fewer texture switches.
        private const int font_atlas_size = 4096;

        private readonly List<IGlyphStore> glyphStores = new List<IGlyphStore>();

        private readonly List<ITexturedGlyphLookupStore> nestedFontStores = new List<ITexturedGlyphLookupStore>();

        private Storage cacheStorage;
        private readonly bool lowResolutionFonts;

        /// <summary>
        /// How text run through this store is shaped before glyphs are fetched.
        /// </summary>
        /// <remarks>
        /// Recorded here so that the shaping path can be selected once per font stack rather than per glyph lookup.
        /// </remarks>
        internal TextShapingMode ShapingMode { get; }

        public TexturePixelFormat AtlasTextureFormat => Atlas?.TextureFormat ?? TexturePixelFormat.R8G8B8A8Float;

        /// <summary>
        /// A local cache to avoid string allocation overhead. Can be changed to (string,char)=>string if this ever becomes an issue,
        /// but as long as we directly inherit <see cref="TextureStore"/> this is a slight optimisation.
        /// </summary>
        private readonly ConcurrentDictionary<(string, char, int), ITexturedCharacterGlyph> namespacedGlyphCache = new ConcurrentDictionary<(string, char, int), ITexturedCharacterGlyph>();

        // Cache the owner, not its resolution: nested stores can have different native pixel sizes.
        private readonly ConcurrentDictionary<(string, char), ITexturedGlyphLookupStore> nestedGlyphOwners = new ConcurrentDictionary<(string, char), ITexturedGlyphLookupStore>();

        /// <summary>
        /// Construct a font store to be added to a parent font store via <see cref="AddStore"/>.
        /// </summary>
        /// <param name="renderer">The renderer to create textures with.</param>
        /// <param name="store">The texture source.</param>
        /// <param name="scaleAdjust">The raw pixel height of the font. Can be used to apply a global scale or metric to font usages.</param>
        public FontStore(IRenderer renderer, IResourceStore<TextureUpload> store = null, float scaleAdjust = 100)
            : this(renderer, store, scaleAdjust, false)
        {
        }

        /// <summary>
        /// Construct a font store with a custom filtering mode to be added to a parent font store via <see cref="AddStore"/>.
        /// All fonts that use the specified filter mode should be nested inside this store to make optimal use of texture atlases.
        /// </summary>
        /// <param name="renderer">The renderer to create textures with.</param>
        /// <param name="store">The texture source.</param>
        /// <param name="scaleAdjust">The raw pixel height of the font. Can be used to apply a global scale or metric to font usages.</param>
        /// <param name="minFilterMode">The texture minification filtering mode to use.</param>
        public FontStore(IRenderer renderer, IResourceStore<TextureUpload> store = null, float scaleAdjust = 100, TextureFilteringMode minFilterMode = TextureFilteringMode.Linear)
            : this(renderer, store, scaleAdjust, true, filteringMode: minFilterMode)
        {
        }

        internal FontStore(IRenderer renderer, IResourceStore<TextureUpload> store = null, float scaleAdjust = 100, bool useAtlas = false, Storage cacheStorage = null,
                           TextureFilteringMode filteringMode = TextureFilteringMode.Linear)
            : base(renderer, store, scaleAdjust: scaleAdjust, useAtlas: false, filteringMode: filteringMode)
        {
            this.cacheStorage = cacheStorage;
            lowResolutionFonts = (renderer as Renderer)?.LowResolutionFonts ?? false;
            ShapingMode = (renderer as Renderer)?.ShapingMode ?? TextShapingMode.Legacy;

            if (useAtlas)
            {
                int size = (renderer as Renderer)?.FontAtlasSize ?? Math.Min(font_atlas_size, renderer.MaxTextureSize);
                bool singleChannel = (renderer as Renderer)?.SingleChannelFontAtlas ?? false;
                Atlas = new TextureAtlas(renderer, size, size, (renderer as Renderer)?.FontAtlasPacking ?? AtlasPackingStrategy.Etagere,
                    manualMipmaps: false, filteringMode: filteringMode,
                    textureFormat: singleChannel ? TexturePixelFormat.R8Float : TexturePixelFormat.R8G8B8A8Float);
            }
        }

        public override void AddTextureSource(IResourceStore<TextureUpload> store)
        {
            if (store is IGlyphStore gs)
            {
                if (gs is IMultiResolutionGlyphStore multiResolution)
                    multiResolution.LowResolutionTextures = lowResolutionFonts;

                if (gs is RawCachingGlyphStore raw && raw.CacheStorage == null)
                    raw.CacheStorage = cacheStorage;

                glyphStores.Add(gs);
                queueLoad(gs);
            }

            base.AddTextureSource(store);
        }

        public override void AddStore(ITextureStore store)
        {
            switch (store)
            {
                case FontStore fs:
                    // if null, share the main store's atlas.
                    fs.Atlas ??= Atlas;
                    fs.cacheStorage ??= cacheStorage;
                    nestedFontStores.Add(fs);
                    break;

                case ITexturedGlyphLookupStore gs:
                    nestedFontStores.Add(gs);
                    break;
            }

            base.AddStore(store);
        }

        private Task childStoreLoadTasks;

        /// <summary>
        /// Append child stores to a single threaded load task.
        /// </summary>
        private void queueLoad(IGlyphStore store)
        {
            var previousLoadStream = childStoreLoadTasks;

            childStoreLoadTasks = Task.Run(async () =>
            {
                if (previousLoadStream != null)
                    await previousLoadStream.ConfigureAwait(false);

                try
                {
                    var sw = Stopwatch.StartNew();
                    await store.LoadFontAsync().ConfigureAwait(false);
                    Logger.Log($"Loaded font {store.FontName} ({sw.ElapsedMilliseconds} ms)", level: LogLevel.Debug);
                }
                catch
                {
                    // Errors are logged by LoadFontAsync() but also propagated outwards.
                    // We can gracefully continue when loading a font fails, so the exception shouldn't trigger the unobserved exception handler of GameHost and potentially crash the game.
                }
            });
        }

        public override void RemoveTextureStore(IResourceStore<TextureUpload> store)
        {
            if (store is IGlyphStore gs)
                glyphStores.Remove(gs);

            base.RemoveTextureStore(store);
        }

        public override void RemoveStore(ITextureStore store)
        {
            if (store is FontStore fs)
                nestedFontStores.Remove(fs);

            base.RemoveStore(store);
        }

        public ITexturedCharacterGlyph Get(string fontName, char character) => GetForSize(fontName, character, 0);

        internal ITexturedCharacterGlyph GetForSize(string fontName, char character, float fontSize)
        {
            int tier = getResolutionTier(fontSize);
            var key = (fontName, character, tier);

            if (namespacedGlyphCache.TryGetValue(key, out var existing))
                return existing;

            if (nestedGlyphOwners.TryGetValue((fontName, character), out var owner))
                return owner?.GetForSize(fontName, character, fontSize);

            foreach (var store in glyphStores)
            {
                if (store.FontName.EndsWith(fontName ?? string.Empty, StringComparison.Ordinal) && store.HasGlyph(character))
                {
                    float textureScale = 1;
                    string textureName = $"{store.FontName}/{character}";

                    if (store is IMultiResolutionGlyphStore multiResolution)
                    {
                        textureScale = multiResolution.GetTextureScale(character, tier);
                        textureName = multiResolution.GetTextureName(character, tier);
                    }

                    return namespacedGlyphCache[key] = new TexturedCharacterGlyph(store.Get(character).AsNonNull(), Get(textureName), 1 / ScaleAdjust,
                        textureScale);
                }
            }

            foreach (var store in nestedFontStores)
            {
                var glyph = store.GetForSize(fontName, character, fontSize);
                if (glyph != null)
                {
                    nestedGlyphOwners[(fontName, character)] = store;
                    return glyph;
                }
            }

            nestedGlyphOwners[(fontName, character)] = null;
            return null;
        }

        /// <summary>
        /// Shapes a run of text with the font matching <paramref name="fontName"/>.
        /// </summary>
        /// <remarks>
        /// A run is shaped with a single font: if that font does not cover every character in it, this returns
        /// <see langword="false"/> rather than emitting <c>.notdef</c> glyphs, because only the per-character path can
        /// substitute glyphs from another font.
        /// </remarks>
        /// <returns><see langword="false"/> when shaping is unavailable or the run could not be shaped.</returns>
        internal bool TryShape(string fontName, string text, float fontSize, out IReadOnlyList<ITexturedCharacterGlyph> glyphs)
        {
            glyphs = Array.Empty<ITexturedCharacterGlyph>();

            if (ShapingMode != TextShapingMode.HarfBuzz || string.IsNullOrEmpty(text))
                return false;

            int tier = getResolutionTier(fontSize);

            foreach (var store in glyphStores)
            {
                if (!store.FontName.EndsWith(fontName ?? string.Empty, StringComparison.Ordinal))
                    continue;

                if (store is not IGlyphShaper shaper || !shaper.TryShape(text, out var shaped))
                    continue;

                float? baseline = store.Baseline;

                if (baseline == null)
                    return false;

                var result = new ITexturedCharacterGlyph[shaped.Count];

                for (int i = 0; i < shaped.Count; i++)
                {
                    ShapedGlyph glyph = shaped[i];

                    var texture = Get(shaper.GetTextureName(glyph.GlyphId, tier));

                    if (texture == null)
                        return false;

                    // A null containing store means this glyph takes no kerning: shaping already applied it.
                    result[i] = new TexturedCharacterGlyph(
                        new CharacterGlyph(glyph.Character, glyph.XOffset, glyph.YOffset, glyph.XAdvance, baseline.Value, null),
                        texture,
                        1 / ScaleAdjust,
                        shaper.GetTextureScale(tier));
                }

                glyphs = result;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The texture resolution multiplier to request for text drawn at <paramref name="fontSize"/>.
        /// Glyph textures are rendered for a size of <see cref="TextureStore.ScaleAdjust"/>; larger text gets 2x or 4x textures from stores that can provide them.
        /// </summary>
        private int getResolutionTier(float fontSize)
        {
            if (lowResolutionFonts || fontSize <= ScaleAdjust)
                return 1;

            return fontSize <= ScaleAdjust * 2 ? 2 : TrueTypeGlyphStore.MAX_TEXTURE_SCALE;
        }

        public Task<ITexturedCharacterGlyph> GetAsync(string fontName, char character) => Task.Run(() => Get(fontName, character));
    }
}
