// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Text;
using SharpFNT;

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// A basic glyph store that will load font sprite sheets every character retrieval.
    /// </summary>
    public class GlyphStore : IResourceStore<TextureUpload>, IGlyphStore
    {
        protected readonly string AssetName;

        protected readonly IResourceStore<TextureUpload> TextureLoader;

        public string FontName { get; }

        public float? Baseline => Font?.Common.Base;

        protected readonly ResourceStore<byte[]> Store;

        [CanBeNull]
        protected BitmapFont Font => completionSource.Task.GetResultSafely();

        private readonly TaskCompletionSource<BitmapFont> completionSource = new TaskCompletionSource<BitmapFont>();

        /// <summary>
        /// This is a rare usage of a static framework-wide cache.
        /// In normal execution font instances are held locally by font stores and this will add no overhead or improvement.
        /// It exists specifically to avoid overheads of parsing fonts repeatedly in unit tests.
        /// </summary>
        private static readonly ConcurrentDictionary<string, BitmapFont> font_cache = new ConcurrentDictionary<string, BitmapFont>();

        /// <summary>
        /// Create a new glyph store.
        /// </summary>
        /// <param name="store">The store to provide font resources.</param>
        /// <param name="assetName">The base name of the font.</param>
        /// <param name="textureLoader">An optional platform-specific store for loading textures. Should load for the store provided in <param ref="param"/>.</param>
        public GlyphStore(ResourceStore<byte[]> store, string assetName = null, IResourceStore<TextureUpload> textureLoader = null)
        {
            Store = new ResourceStore<byte[]>(store);

            Store.AddExtension("fnt");
            Store.AddExtension("bin");

            AssetName = assetName;
            TextureLoader = textureLoader;
            asciiHighDefinitionStore = new Lazy<GlyphStore>(loadHighDefinitionStore);

            FontName = assetName?.Split('/').Last() ?? string.Empty;
            resourceNamePrefix = $"{FontName}/";
            highDefinitionResourceNamePrefix = $"{resourceNamePrefix}{HIGH_DEFINITION_PREFIX}";
        }

        private Task fontLoadTask;
        private readonly Lazy<GlyphStore> asciiHighDefinitionStore;
        private readonly string resourceNamePrefix;
        private readonly string highDefinitionResourceNamePrefix;

        internal const string HIGH_DEFINITION_PREFIX = "ASCII-HD/";

        internal bool LowResolutionTextures { get; set; }

        /// <summary>
        /// Texture resolution multiplier for a printable ASCII glyph. Layout metrics remain those of the original font.
        /// </summary>
        public float GetTextureScale(char character) => GetTextureScale(character, false);

        internal float GetTextureScale(char character, bool highDefinition)
        {
            if (LowResolutionTextures)
                return 0.5f;

            if (!highDefinition || character < ' ' || character > '~')
                return 1;

            var companion = asciiHighDefinitionStore.Value;
            return companion?.HasGlyph(character) == true ? (float)companion.Font.Common.LineHeight / Font.Common.LineHeight : 1;
        }

        private GlyphStore loadHighDefinitionStore()
        {
            // Resolve only after a large glyph is requested. Never recursively probe companions.
            if (Font == null || AssetName?.EndsWith("-ASCII-HD", StringComparison.Ordinal) == true)
                return null;

            using var stream = Store.GetStream($"{AssetName}-ASCII-HD");
            if (stream == null)
                return null;

            var candidate = new GlyphStore(Store, $"{AssetName}-ASCII-HD", TextureLoader);
            try
            {
                candidate.LoadFontAsync().GetAwaiter().GetResult();
                if (candidate.Font.Common.LineHeight > Font.Common.LineHeight)
                    return candidate;
            }
            catch
            {
                // A broken optional companion must not prevent loading the original font.
            }

            candidate.Dispose();
            return null;
        }

        public Task LoadFontAsync() => fontLoadTask ??= Task.Factory.StartNew(() =>
        {
            try
            {
                BitmapFont font;

                using (var s = Store.GetStream($@"{AssetName}"))
                {
                    string hash = s.ComputeMD5Hash();

                    if (font_cache.TryGetValue(hash, out font))
                    {
                        Logger.Log($"Cached font load for {AssetName}");
                    }
                    else
                    {
                        font_cache.TryAdd(hash, font = BitmapFont.FromStream(s, FormatHint.Binary, false));
                    }
                }

                completionSource.SetResult(font);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Couldn't load font asset from {AssetName}.");
                completionSource.SetResult(null);
                throw;
            }
        }, TaskCreationOptions.PreferFairness);

        public bool HasGlyph(char c) => Font?.Characters.ContainsKey(c) == true;

        protected virtual TextureUpload GetPageImage(int page)
        {
            if (TextureLoader != null)
                return TextureLoader.Get(GetFilenameForPage(page));

            using (var stream = Store.GetStream(GetFilenameForPage(page)))
                return new TextureUpload(stream);
        }

        protected string GetFilenameForPage(int page)
        {
            Debug.Assert(Font != null);
            return $@"{AssetName}_{page.ToString().PadLeft((Font.Pages.Count - 1).ToString().Length, '0')}.png";
        }

        public CharacterGlyph Get(char character)
        {
            if (Font == null)
                return null;

            Debug.Assert(Baseline != null);

            var bmCharacter = Font.GetCharacter(character);

            return new CharacterGlyph(character, bmCharacter.XOffset, bmCharacter.YOffset, bmCharacter.XAdvance, Baseline.Value, this);
        }

        public int GetKerning(char left, char right) => Font?.GetKerningAmount(left, right) ?? 0;

        Task<CharacterGlyph> IResourceStore<CharacterGlyph>.GetAsync(string name, CancellationToken cancellationToken) =>
            Task.Run(() => ((IGlyphStore)this).Get(name[0]), cancellationToken);

        CharacterGlyph IResourceStore<CharacterGlyph>.Get(string name) => Get(name[0]);

        public TextureUpload Get(string name)
        {
            if (Font == null) return null;

            if (name.Length > 1 && !name.StartsWith(resourceNamePrefix, StringComparison.Ordinal))
                return null;

            char character = name.Last();
            if (GetTextureScale(character, name.StartsWith(highDefinitionResourceNamePrefix, StringComparison.Ordinal)) > 1)
                return asciiHighDefinitionStore.Value.Get(character.ToString());

            return Font.Characters.TryGetValue(character, out Character c) ? LoadCharacter(c) : null;
        }

        public virtual async Task<TextureUpload> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            if (name.Length > 1 && !name.StartsWith(resourceNamePrefix, StringComparison.Ordinal))
                return null;

            var bmFont = await completionSource.Task.ConfigureAwait(false);

            char character = name.Last();
            if (GetTextureScale(character, name.StartsWith(highDefinitionResourceNamePrefix, StringComparison.Ordinal)) > 1)
                return await asciiHighDefinitionStore.Value.GetAsync(character.ToString(), cancellationToken).ConfigureAwait(false);

            return bmFont.Characters.TryGetValue(character, out Character c)
                ? LoadCharacter(c)
                : null;
        }

        protected int LoadedGlyphCount;

        protected virtual TextureUpload LoadCharacter(Character character)
        {
            var page = GetPageImage(character.Page);
            if (page == null)
                return null;

            LoadedGlyphCount++;

            var source = page.Data;

            // the spritesheet may have unused pixels trimmed
            int readableHeight = Math.Min(character.Height, page.Height - character.Y);
            int readableWidth = Math.Min(character.Width, page.Width - character.X);

            int scale = LowResolutionTextures ? 2 : 1;
            int width = (character.Width + scale - 1) / scale;
            int height = (character.Height + scale - 1) / scale;
            byte[] coverage = new byte[checked(width * height)];

            if (!LowResolutionTextures)
            {
                // The coverage array is zero-initialised, so trimmed source pixels remain transparent.
                for (int y = 0; y < readableHeight; y++)
                {
                    int sourceRow = (character.Y + y) * page.Width + character.X;
                    int destinationRow = y * width;

                    for (int x = 0; x < readableWidth; x++)
                        coverage[destinationRow + x] = source[sourceRow + x].A;
                }

                return new AlphaTextureUpload(width, height, coverage);
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int total = 0;
                    int samples = 0;

                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int sourceX = x * scale + sx;
                            int sourceY = y * scale + sy;
                            if (sourceX >= readableWidth || sourceY >= readableHeight)
                                continue;

                            total += source[(character.Y + sourceY) * page.Width + character.X + sourceX].A;
                            samples++;
                        }

                    coverage[y * width + x] = samples == 0 ? (byte)0 : (byte)((total + samples / 2) / samples);
                }
            }

            return new AlphaTextureUpload(width, height, coverage);
        }

        public Stream GetStream(string name) => throw new NotSupportedException();

        public IEnumerable<string> GetAvailableResources() => Font?.Characters.Keys.Select(k => $"{FontName}/{(char)k}") ?? Enumerable.Empty<string>();

        #region IDisposable Support

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && asciiHighDefinitionStore.IsValueCreated)
                asciiHighDefinitionStore.Value?.Dispose();
        }

        #endregion
    }
}
