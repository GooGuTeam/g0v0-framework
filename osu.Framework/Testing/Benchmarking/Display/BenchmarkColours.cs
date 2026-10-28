// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;

namespace osu.Framework.Testing.Benchmarking.Display
{
    internal static class BenchmarkColours
    {
        public static readonly Colour4 BACKGROUND = Colour4.FromHex(@"101217");
        public static readonly Colour4 PANEL = Colour4.FromHex(@"181b22");
        public static readonly Colour4 PANEL_RAISED = Colour4.FromHex(@"20242d");
        public static readonly Colour4 PANEL_HOVER = Colour4.FromHex(@"282d38");
        public static readonly Colour4 BORDER = Colour4.FromHex(@"2e3440");

        public static readonly Colour4 TEXT = Colour4.FromHex(@"e8eaed");
        public static readonly Colour4 TEXT_DIM = Colour4.FromHex(@"8b93a1");
        public static readonly Colour4 TEXT_FAINT = Colour4.FromHex(@"5c6370");

        public static readonly Colour4 ACCENT = Colour4.FromHex(@"6ea8fe");
        public static readonly Colour4 FASTER = Colour4.FromHex(@"3dd68c");
        public static readonly Colour4 SLOWER = Colour4.FromHex(@"ff6b6b");
        public static readonly Colour4 WARNING = Colour4.FromHex(@"ffb547");

        public static readonly Colour4 UPDATE = Colour4.FromHex(@"5b8def");
        public static readonly Colour4 DRAW = Colour4.FromHex(@"b07cf2");
        public static readonly Colour4 GPU = Colour4.FromHex(@"f2a65a");

        public static FontUsage Font(float size, bool bold = false, bool condensed = false)
            => new FontUsage(condensed ? @"RobotoCondensed" : @"Roboto", size, bold ? @"Bold" : @"Regular");

        /// <summary>
        /// The colour of a change from the baseline: green when faster, red when slower, grey when within noise.
        /// </summary>
        public static Colour4 ForChange(double change, bool significant)
            => !significant ? TEXT_DIM : change < 0 ? FASTER : SLOWER;
    }
}
