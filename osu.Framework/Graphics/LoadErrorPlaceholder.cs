// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Development;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace osu.Framework.Graphics
{
    /// <summary>
    /// A drawable which is used in place of a component which threw an exception while loading, when <see cref="LoadErrorHandling"/> is enabled.
    /// </summary>
    /// <remarks>
    /// This only ever appears in debug builds (or when <see cref="LoadErrorHandling"/> is explicitly enabled), and is intended to make a
    /// failing component immediately visible without taking the rest of the game down with it. The full exception is always logged
    /// when this placeholder is created.
    /// </remarks>
    public partial class LoadErrorPlaceholder : CompositeDrawable
    {
        /// <summary>
        /// The component which failed to load, and was replaced by this placeholder.
        /// </summary>
        public Drawable FailedDrawable { get; }

        /// <summary>
        /// The exception which was thrown while loading <see cref="FailedDrawable"/>.
        /// </summary>
        public Exception Failure { get; }

        public LoadErrorPlaceholder(Drawable failedDrawable, Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failedDrawable);
            ArgumentNullException.ThrowIfNull(failure);

            FailedDrawable = failedDrawable;
            Failure = failure;

            // auto-size by default, such that the placeholder is always visible. the parent creating this placeholder may override the sizing
            // to match the component which failed (see CompositeDrawable.CreateLoadErrorPlaceholder).
            AutoSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Colour4.Orange,
                    Alpha = 0.35f,
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding(8),
                    Spacing = new Vector2(0, 2),
                    Children = new Drawable[]
                    {
                        new SpriteText
                        {
                            Text = $"{failedDrawable.GetType().ReadableName()} failed to load",
                            Font = FrameworkFont.Condensed.With(size: 16),
                        },
                        new SpriteText
                        {
                            Text = $"{failure.GetType().Name}: {summarise(failure.Message)}",
                            Font = FrameworkFont.Condensed.With(size: 12),
                        },
                    },
                },
            };
        }

        private const int max_message_length = 180;

        private static string summarise(string message)
        {
            // messages can be arbitrarily long (and multi-line), which would make for a very unwieldy placeholder.
            string singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();

            return singleLine.Length > max_message_length ? $"{singleLine[..max_message_length]}..." : singleLine;
        }
    }
}
