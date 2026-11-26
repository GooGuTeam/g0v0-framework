// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using HarfBuzzSharp;
using NUnit.Framework;
using osu.Framework.Configuration;
using osu.Framework.IO.Stores;
using osu.Framework.Text;

namespace osu.Framework.Tests.Text
{
    /// <summary>
    /// Checks that the HarfBuzz native library loads and shapes on every platform in the test matrix.
    /// </summary>
    /// <remarks>
    /// HarfBuzzSharp resolves its native library per platform at first use, so a missing or mis-packaged asset only
    /// surfaces at runtime. Shaping a real font here fails the build instead of failing on a player's machine.
    /// </remarks>
    [TestFixture]
    public class TextShapingSmokeTest
    {
        private byte[] fontData = null!;

        [SetUp]
        public void SetUp()
        {
            var store = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(TextShapingSmokeTest).Assembly), "Resources/Fonts/Figtree");

            fontData = store.Get("Figtree-Regular.ttf")
                       ?? throw new InvalidOperationException("Figtree-Regular.ttf is missing from the test resources.");
        }

        [Test]
        public void TestShapesFromMemory()
        {
            var (glyphs, advance) = shape("AV");

            Assert.That(glyphs, Has.Length.EqualTo(2), "Each letter should map to exactly one glyph through the font's cmap.");
            Assert.That(glyphs, Has.All.GreaterThan(0u), "A shaped glyph index of zero means the character was not found.");
            Assert.That(advance, Is.GreaterThan(0), "A shaped run must advance horizontally.");
        }

        [Test]
        public void TestShapingIsDeterministic()
        {
            var first = shape("AV");
            var second = shape("AV");

            Assert.That(second.glyphs, Is.EqualTo(first.glyphs));
            Assert.That(second.advance, Is.EqualTo(first.advance));
        }

        /// <summary>
        /// Combining marks are the case the current per-code-unit path cannot position, so they anchor the behaviour
        /// the shaped path is expected to change once it is wired in.
        /// </summary>
        [Test]
        public void TestShapesCombiningMarkSequence()
        {
            var (glyphs, advance) = shape("e\u0301");

            Assert.That(glyphs, Is.Not.Empty, "A decomposed sequence must still produce glyphs.");
            Assert.That(advance, Is.GreaterThan(0), "The base letter must still advance; a combining mark alone does not.");
        }

        [Test]
        public void TestDefaultShapingModeIsLegacy()
        {
            Assert.That(FrameworkFeatureFlags.TextShaping, Is.EqualTo(TextShapingMode.Legacy),
                "Shaping must stay opt-in so existing layouts are unaffected until the shaped path is ready.");
        }

        private (uint[] glyphs, int advance) shape(string text)
        {
            using var blob = Blob.FromStream(new MemoryStream(fontData));
            using var face = new Face(blob, 0);
            using var font = new Font(face);
            font.SetScale(face.UnitsPerEm, face.UnitsPerEm);

            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(text);
            buffer.GuessSegmentProperties();
            font.Shape(buffer);

            return (buffer.GlyphInfos.Select(i => i.Codepoint).ToArray(), buffer.GlyphPositions.Sum(p => p.XAdvance));
        }
    }
}
