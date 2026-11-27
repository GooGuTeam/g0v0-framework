// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FreeTypeSharp;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Text;
using static FreeTypeSharp.FT;

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// A glyph store that rasterises glyphs from a TrueType / OpenType font file on demand, using FreeType.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Glyph metrics use the same pixel space as an AngelCode BMFont generated with a positive font size of <see cref="LineHeight"/>
    /// (the font's ascender-to-descender cell is <see cref="LineHeight"/> pixels), so a TrueType font can replace a pre-rendered
    /// bitmap font of the same name without changing text layout.
    /// </para>
    /// <para>
    /// Textures are rendered only for characters that are actually displayed, at the resolution tier the <see cref="FontStore"/>
    /// requests for the drawn size, so large text stays sharp without shipping pre-rendered high-resolution sheets.
    /// </para>
    /// </remarks>
    public unsafe class TrueTypeGlyphStore : IResourceStore<TextureUpload>, IMultiResolutionGlyphStore, IGlyphShaper
    {
        /// <summary>
        /// The font file extensions looked up for an asset name, in order.
        /// </summary>
        public static readonly IReadOnlyList<string> EXTENSIONS = new[] { "ttf", "otf" };

        /// <summary>
        /// The highest texture resolution multiplier rendered for large text.
        /// </summary>
        public const int MAX_TEXTURE_SCALE = 4;

        private const string resolution_prefix = "@";
        private const string resolution_suffix = "x/";

        /// <summary>
        /// Marks a texture name that refers to a glyph index rather than to a character.
        /// </summary>
        private const string glyph_prefix = "#";

        public string FontName { get; }

        public float? Baseline { get; private set; }

        /// <summary>
        /// The height in pixels of the font's ascender-to-descender cell at texture scale 1.
        /// </summary>
        public float LineHeight { get; }

        internal bool LowResolutionTextures { get; set; }

        bool IMultiResolutionGlyphStore.LowResolutionTextures
        {
            get => LowResolutionTextures;
            set => LowResolutionTextures = value;
        }

        private readonly ResourceStore<byte[]> store;
        private readonly string assetName;
        private readonly string resourceNamePrefix;
        private readonly object faceLock = new object();

        private readonly ConcurrentDictionary<char, CharacterGlyph?> glyphCache = new ConcurrentDictionary<char, CharacterGlyph?>();
        private readonly ConcurrentDictionary<(char, char), float> kerningCache = new ConcurrentDictionary<(char, char), float>();

        private Task? loadTask;
        private GCHandle dataHandle;
        private FT_LibraryRec_* library;
        private FT_FaceRec_* face;
        private OpenTypeKerning? kerning;
        private bool hasKernTable;

        /// <summary>
        /// Serialises shaping. A HarfBuzz font carries mutable state and must not be used concurrently.
        /// </summary>
        /// <remarks>
        /// Where both locks are needed the order is always <see cref="shapingLock"/> then <see cref="faceLock"/>.
        /// </remarks>
        private readonly object shapingLock = new object();

        private HarfBuzzSharp.Blob? harfBuzzBlob;
        private HarfBuzzSharp.Face? harfBuzzFace;
        private HarfBuzzSharp.Font? harfBuzzFont;

        /// <summary>
        /// Pixels per font unit at texture scale 1.
        /// </summary>
        private float unitScale;

        /// <summary>
        /// The em size in pixels at texture scale 1.
        /// </summary>
        private float emSize;

        private float currentSize = float.NaN;
        private bool disposed;

        /// <summary>
        /// Create a new TrueType glyph store.
        /// </summary>
        /// <param name="store">The store providing the font file.</param>
        /// <param name="assetName">The font's asset name without extension, such as <c>Fonts/Inter/Inter-Bold</c>. <see cref="EXTENSIONS"/> are tried in order.</param>
        /// <param name="lineHeight">The height in pixels of the font's ascender-to-descender cell at texture scale 1.</param>
        public TrueTypeGlyphStore(ResourceStore<byte[]> store, string assetName, float lineHeight = 100)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrEmpty(assetName);

            this.store = store;
            this.assetName = assetName;
            LineHeight = lineHeight;

            FontName = assetName.Split('/').Last();
            resourceNamePrefix = $"{FontName}/";
        }

        /// <summary>
        /// Whether <paramref name="store"/> provides a TrueType / OpenType font file for <paramref name="assetName"/>.
        /// </summary>
        public static bool Exists(IResourceStore<byte[]> store, string assetName)
        {
            foreach (string extension in EXTENSIONS)
            {
                using (var stream = store.GetStream($"{assetName}.{extension}"))
                {
                    if (stream != null)
                        return true;
                }
            }

            return false;
        }

        public Task LoadFontAsync() => loadTask ??= Task.Factory.StartNew(load, TaskCreationOptions.PreferFairness);

        private void load()
        {
            byte[]? data = null;

            foreach (string extension in EXTENSIONS)
            {
                data = store.Get($"{assetName}.{extension}");
                if (data != null)
                    break;
            }

            if (data == null)
                throw new FileNotFoundException($"Couldn't find a font file for {assetName}.");

            lock (faceLock)
            {
                if (disposed)
                    return;

                try
                {
                    FT_LibraryRec_* lib;
                    check(FT_Init_FreeType(&lib), "initialise FreeType");
                    library = lib;

                    // FreeType reads the font from this memory for the face's lifetime.
                    dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);

                    FT_FaceRec_* f;
                    check(FT_New_Memory_Face(library, (byte*)dataHandle.AddrOfPinnedObject(), data.Length, 0, &f), $"load {assetName}");
                    face = f;

                    int cell = face->ascender - face->descender;
                    if (cell <= 0)
                        throw new InvalidDataException($"{assetName} has no vertical metrics.");

                    unitScale = LineHeight / cell;
                    emSize = face->units_per_EM * unitScale;
                    Baseline = face->ascender * unitScale;

                    kerning = OpenTypeKerning.TryCreate(data);
                    hasKernTable = kerning == null && ((long)face->face_flags & (1 << 6)) != 0; // FT_FACE_FLAG_KERNING

                    initialiseShaping(data, face->units_per_EM);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"Couldn't load font {assetName}.");
                    releaseFace();
                    throw;
                }
            }
        }

        /// <summary>
        /// Waits for the font to load, like <see cref="GlyphStore"/> does on first use. A failed load leaves the store empty.
        /// </summary>
        private bool ensureLoaded()
        {
            try
            {
                LoadFontAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Already logged by load().
            }

            return face != null;
        }

        /// <summary>
        /// Prepares the HarfBuzz objects used for shaping.
        /// </summary>
        /// <remarks>
        /// Shaping is an optional capability: if it cannot be prepared, this store keeps working through the
        /// per-character path, so a failure here must not fail the font load.
        /// </remarks>
        private void initialiseShaping(byte[] data, int unitsPerEm)
        {
            try
            {
                harfBuzzBlob = HarfBuzzSharp.Blob.FromStream(new MemoryStream(data, writable: false));
                harfBuzzFace = new HarfBuzzSharp.Face(harfBuzzBlob, 0);
                harfBuzzFont = new HarfBuzzSharp.Font(harfBuzzFace);

                // Shaping in font units puts advances and offsets in the same space as the FreeType metrics, so
                // unitScale can convert both to pixels.
                harfBuzzFont.SetScale(unitsPerEm, unitsPerEm);
            }
            catch (Exception e)
            {
                Logger.Log($"Couldn't prepare text shaping for {assetName}: {e.Message}", level: LogLevel.Debug);
                disposeShaping();
            }
        }

        private void disposeShaping()
        {
            harfBuzzFont?.Dispose();
            harfBuzzFont = null;
            harfBuzzFace?.Dispose();
            harfBuzzFace = null;
            harfBuzzBlob?.Dispose();
            harfBuzzBlob = null;
        }

        bool IGlyphShaper.TryShape(string text, out IReadOnlyList<ShapedGlyph> glyphs) => tryShape(text, out glyphs);

        private bool tryShape(string text, out IReadOnlyList<ShapedGlyph> glyphs)
        {
            glyphs = Array.Empty<ShapedGlyph>();

            if (string.IsNullOrEmpty(text) || !ensureLoaded())
                return false;

            lock (shapingLock)
            {
                // Re-check under the lock: the font may have failed to load, or been disposed, in the meantime.
                var font = harfBuzzFont;
                if (font == null || Baseline == null)
                    return false;

                try
                {
                    return tryShapeRun(font, text, out glyphs);
                }
                catch (Exception e)
                {
                    // Shaping must never take down rendering: give up on it for this font and let the per-character
                    // path handle the rest of the session.
                    Logger.Error(e, $"Couldn't shape text with {assetName}; falling back to per-character layout.");
                    disposeShaping();
                    glyphs = Array.Empty<ShapedGlyph>();
                    return false;
                }
            }
        }

        private bool tryShapeRun(HarfBuzzSharp.Font font, string text, out IReadOnlyList<ShapedGlyph> glyphs)
        {
            // One buffer carries one script: HarfBuzz guesses it from the first character that has one and then applies
            // that script's rules to the whole buffer, so text that mixes scripts has to be shaped once per script.
            var boundaries = ScriptRuns.GetBoundaries(text);

            if (boundaries.Count == 2)
                return tryShapeSegment(font, text, 0, text.Length, out glyphs);

            var all = new List<ShapedGlyph>(text.Length);

            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                int start = boundaries[i];

                if (!tryShapeSegment(font, text, start, boundaries[i + 1] - start, out var segment))
                {
                    glyphs = Array.Empty<ShapedGlyph>();
                    return false;
                }

                all.AddRange(segment);
            }

            glyphs = all;
            return true;
        }

        /// <summary>
        /// Shapes the <paramref name="length"/> characters of <paramref name="text"/> starting at <paramref name="start"/>,
        /// which must all belong to one script and read in one direction.
        /// </summary>
        private bool tryShapeSegment(HarfBuzzSharp.Font font, string text, int start, int length, out IReadOnlyList<ShapedGlyph> glyphs)
        {
            glyphs = Array.Empty<ShapedGlyph>();

            string segment = length == text.Length ? text : text.Substring(start, length);

            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(segment);
            buffer.GuessSegmentProperties();
            font.Shape(buffer);

            var infos = buffer.GlyphInfos;
            var positions = buffer.GlyphPositions;

            if (infos.Length == 0)
                return false;

            ShapedGlyph[] shaped = new ShapedGlyph[infos.Length];
            int[] clusters = new int[infos.Length];
            var clusterLengths = new Dictionary<int, int>(infos.Length);

            // HarfBuzz reports the offset each cluster starts at, not its length. Clusters tile the text in logical
            // order, so a cluster ends where the next one begins, whatever order the glyphs were returned in.
            for (int i = 0; i < infos.Length; i++)
                clusters[i] = Math.Clamp((int)infos[i].Cluster, 0, segment.Length - 1);

            int[] orderedClusters = (int[])clusters.Clone();
            Array.Sort(orderedClusters);

            for (int i = 0; i < orderedClusters.Length; i++)
            {
                if (i + 1 < orderedClusters.Length && orderedClusters[i] == orderedClusters[i + 1])
                    continue;

                int nextCluster = i + 1 < orderedClusters.Length ? orderedClusters[i + 1] : segment.Length;
                clusterLengths[orderedClusters[i]] = nextCluster - orderedClusters[i];
            }

            lock (faceLock)
            {
                if (face == null || Baseline == null)
                    return false;

                float baseline = Baseline.Value;

                for (int i = 0; i < infos.Length; i++)
                {
                    uint glyphId = infos[i].Codepoint;

                    // Zero is .notdef: the run needs the per-character path, which can fall back to another font.
                    if (glyphId == 0 || !renderGlyph(glyphId, 1))
                        return false;

                    var slot = face->glyph;

                    shaped[i] = new ShapedGlyph(
                        glyphId,
                        segment[clusters[i]],
                        // Reported against the whole text, so that a line made of several scripts keeps one numbering.
                        start + clusters[i],
                        clusterLengths[clusters[i]],
                        positions[i].XAdvance * unitScale,
                        slot->bitmap_left + positions[i].XOffset * unitScale,
                        // HarfBuzz measures vertical offsets upwards, the framework downwards from the line top.
                        baseline - slot->bitmap_top - positions[i].YOffset * unitScale);
                }
            }

            glyphs = shaped;
            return true;
        }

        string IGlyphShaper.GetTextureName(uint glyphId, int tier) => getTextureName(glyphId, tier);

        private string getTextureName(uint glyphId, int tier)
        {
            float scale = getTextureScale(tier);
            string name = $"{glyph_prefix}{glyphId}";

            return scale == 1
                ? $"{resourceNamePrefix}{name}"
                : $"{resourceNamePrefix}{resolution_prefix}{scale.ToString(CultureInfo.InvariantCulture)}{resolution_suffix}{name}";
        }

        float IGlyphShaper.GetTextureScale(int tier) => getTextureScale(tier);

        private float getTextureScale(int tier) => LowResolutionTextures ? 0.5f : Math.Clamp(tier, 1, MAX_TEXTURE_SCALE);

        public bool HasGlyph(char c)
        {
            if (!ensureLoaded())
                return false;

            lock (faceLock)
                return face != null && FT_Get_Char_Index(face, c) != 0;
        }

        public CharacterGlyph? Get(char character) => glyphCache.GetOrAdd(character, loadGlyph);

        private CharacterGlyph? loadGlyph(char character)
        {
            if (!ensureLoaded())
                return null;

            lock (faceLock)
            {
                if (face == null || Baseline == null)
                    return null;

                uint index = FT_Get_Char_Index(face, character);
                if (index == 0)
                    return null;

                // Metrics come from the rendered bitmap at scale 1, so they describe exactly what is drawn.
                if (!renderGlyph(index, 1))
                    return null;

                var slot = face->glyph;
                float advance = slot->linearHoriAdvance / 65536f;

                return new CharacterGlyph(character, slot->bitmap_left, Baseline.Value - slot->bitmap_top, advance, Baseline.Value, this);
            }
        }

        public int GetKerning(char left, char right) => (int)MathF.Round(getKerning(left, right));

        private float getKerning(char left, char right)
        {
            if (!ensureLoaded() || (kerning == null && !hasKernTable))
                return 0;

            return kerningCache.GetOrAdd((left, right), pair =>
            {
                lock (faceLock)
                {
                    if (face == null)
                        return 0;

                    uint l = FT_Get_Char_Index(face, pair.Item1);
                    uint r = FT_Get_Char_Index(face, pair.Item2);
                    if (l == 0 || r == 0)
                        return 0;

                    if (kerning != null)
                        return kerning.Get(l, r) * unitScale;

                    FT_Vector_ delta;
                    if (FT_Get_Kerning(face, l, r, FT_Kerning_Mode_.FT_KERNING_UNSCALED, &delta) != FT_Error.FT_Err_Ok)
                        return 0;

                    return delta.x * unitScale;
                }
            });
        }

        public float GetTextureScale(char character, int tier) => getTextureScale(tier);

        public string GetTextureName(char character, int tier)
        {
            float scale = GetTextureScale(character, tier);

            // Scale 1 keeps the plain name, which is also how glyphs are looked up by name alone.
            return scale == 1
                ? $"{resourceNamePrefix}{character}"
                : $"{resourceNamePrefix}{resolution_prefix}{scale.ToString(CultureInfo.InvariantCulture)}{resolution_suffix}{character}";
        }

        public TextureUpload Get(string name)
        {
            if (!tryParseTextureName(name, out char character, out uint glyphId, out float scale) || !ensureLoaded())
                return null!;

            lock (faceLock)
            {
                if (face == null)
                    return null!;

                uint index = glyphId != 0 ? glyphId : FT_Get_Char_Index(face, character);
                if (index == 0 || !renderGlyph(index, scale))
                    return null!;

                var bitmap = face->glyph->bitmap;
                int width = (int)bitmap.width;
                int height = (int)bitmap.rows;

                // Blank glyphs (spaces) have no bitmap; textures cannot be empty, so give them one transparent pixel, like BMFont does.
                if (width == 0 || height == 0)
                    return new AlphaTextureUpload(1, 1, new byte[1]);

                byte[] coverage = new byte[width * height];

                for (int y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<byte>(bitmap.buffer + (long)y * bitmap.pitch, width);
                    row.CopyTo(coverage.AsSpan(y * width, width));
                }

                return new AlphaTextureUpload(width, height, coverage);
            }
        }

        public Task<TextureUpload> GetAsync(string name, CancellationToken cancellationToken = default) => Task.Run(() => Get(name), cancellationToken);

        private bool tryParseTextureName(string name, out char character, out uint glyphId, out float scale)
        {
            character = default;
            glyphId = 0;
            scale = 1;

            if (name.Length == 1)
            {
                character = name[0];
                return true;
            }

            if (!name.StartsWith(resourceNamePrefix, StringComparison.Ordinal) || name.Length <= resourceNamePrefix.Length)
                return false;

            var rest = name.AsSpan(resourceNamePrefix.Length);

            if (rest.Length > 1 && rest.StartsWith(resolution_prefix, StringComparison.Ordinal))
            {
                int end = rest.IndexOf(resolution_suffix, StringComparison.Ordinal);
                if (end < 0 || !float.TryParse(rest[resolution_prefix.Length..end], NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0)
                    return false;

                rest = rest[(end + resolution_suffix.Length)..];
            }

            if (rest.Length > 1 && rest.StartsWith(glyph_prefix, StringComparison.Ordinal))
                return uint.TryParse(rest[glyph_prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out glyphId) && glyphId != 0;

            if (rest.Length != 1)
                return false;

            character = rest[0];
            return true;
        }

        /// <summary>
        /// Loads and renders a glyph into the face's glyph slot. Must be called under <see cref="faceLock"/>.
        /// </summary>
        private bool renderGlyph(uint index, float scale)
        {
            float size = emSize * scale;

            if (size != currentSize)
            {
                if (FT_Set_Char_Size(face, 0, (nint)MathF.Round(size * 64), 72, 72) != FT_Error.FT_Err_Ok)
                    return false;

                currentSize = size;
            }

            // Unhinted outlines scale consistently between resolution tiers; at these sizes hinting gains nothing.
            if (FT_Load_Glyph(face, index, FT_LOAD.FT_LOAD_NO_HINTING | FT_LOAD.FT_LOAD_NO_BITMAP) != FT_Error.FT_Err_Ok)
                return false;

            return FT_Render_Glyph(face->glyph, FT_Render_Mode_.FT_RENDER_MODE_NORMAL) == FT_Error.FT_Err_Ok;
        }

        private static void check(FT_Error error, string action)
        {
            if (error != FT_Error.FT_Err_Ok)
                throw new InvalidOperationException($"FreeType failed to {action}: {error}.");
        }

        CharacterGlyph IResourceStore<CharacterGlyph>.Get(string name) => Get(name[0])!;

        Task<CharacterGlyph> IResourceStore<CharacterGlyph>.GetAsync(string name, CancellationToken cancellationToken) =>
            Task.Run(() => Get(name[0])!, cancellationToken);

        public Stream GetStream(string name) => throw new NotSupportedException();

        public IEnumerable<string> GetAvailableResources() => glyphCache.Where(g => g.Value != null).Select(g => $"{FontName}/{g.Key}");

        private void releaseFace()
        {
            disposeShaping();

            if (face != null)
            {
                FT_Done_Face(face);
                face = null;
            }

            if (library != null)
            {
                FT_Done_FreeType(library);
                library = null;
            }

            if (dataHandle.IsAllocated)
                dataHandle.Free();
        }

        #region IDisposable Support

        ~TrueTypeGlyphStore()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // The same order as shaping takes them, so that a shape in flight cannot deadlock against disposal.
            lock (shapingLock)
            {
                lock (faceLock)
                {
                    if (disposed)
                        return;

                    disposed = true;
                    releaseFace();
                }
            }
        }

        #endregion
    }
}
