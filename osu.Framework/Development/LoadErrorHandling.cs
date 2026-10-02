// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;

namespace osu.Framework.Development
{
    /// <summary>
    /// Controls how a <see cref="Drawable"/> which throws an exception while being loaded is handled.
    /// </summary>
    public static class LoadErrorHandling
    {
        /// <summary>
        /// Whether a <see cref="Drawable"/> which throws while loading should be isolated and replaced with a placeholder drawable.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="DebugUtils.IsDebugBuild"/> on non-test runs. Note that tests which intend to verify the
        /// placeholder behavior have to enable this explicitly, while tests which intend to verify that a load failure is
        /// reported have to leave it disabled (the default).
        /// </remarks>
        public static bool Enabled { get; set; } = DebugUtils.IsDebugBuild && !DebugUtils.IsNUnitRunning;

        /// <summary>
        /// An optional factory used to create the placeholder drawable which replaces a failed component.
        /// </summary>
        /// <remarks>
        /// Defaults to <see langword="null"/>, in which case a <see cref="LoadErrorPlaceholder"/> is used.
        /// This is the intended hook for games which want to present load failures in their own style (or report them elsewhere),
        /// and can be set once at startup.
        /// </remarks>
        public static Func<Drawable, Exception, Drawable>? PlaceholderFactory { get; set; }
    }
}
