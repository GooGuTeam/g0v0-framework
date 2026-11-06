// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;

namespace osu.Framework.Testing.Benchmarking.Display
{
    /// <summary>
    /// A pill that switches whether a factor is varied in the next run.
    /// </summary>
    internal partial class FactorChip : CompositeDrawable
    {
        public readonly BindableBool Included = new BindableBool();
        public readonly BindableBool Locked = new BindableBool();

        private readonly Box background;
        private readonly SpriteText label;
        private readonly SpriteText state;

        public FactorChip(BenchmarkFactor factor)
        {
            AutoSizeAxes = Axes.X;
            Height = 30;
            Masking = true;
            CornerRadius = 15;
            BorderThickness = 1.5f;

            InternalChildren = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Direction = FillDirection.Horizontal,
                    Spacing = new osuTK.Vector2(6, 0),
                    Margin = new MarginPadding { Horizontal = 13 },
                    Children = new Drawable[]
                    {
                        label = new SpriteText
                        {
                            Text = factor.Name,
                            Font = BenchmarkColours.Font(14, bold: true),
                        },
                        state = new SpriteText
                        {
                            // Varying a factor turns it from its default to the other state.
                            Text = factor.EnabledByDefault ? "turn off" : "turn on",
                            Font = BenchmarkColours.Font(13, condensed: true),
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Included.BindValueChanged(_ => updateState(), true);
            Locked.BindValueChanged(_ => updateState(), true);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (!Locked.Value)
                Included.Toggle();

            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateState();
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e) => updateState();

        private void updateState()
        {
            var colour = Included.Value ? BenchmarkColours.ACCENT : BenchmarkColours.TEXT_FAINT;

            BorderColour = colour;
            background.Colour = Included.Value ? colour.Opacity(IsHovered ? 0.3f : 0.2f) : BenchmarkColours.PANEL.Lighten(IsHovered ? 0.2f : 0);
            label.Colour = Included.Value ? BenchmarkColours.TEXT : BenchmarkColours.TEXT_DIM;
            state.Colour = Included.Value ? BenchmarkColours.ACCENT : BenchmarkColours.TEXT_FAINT;
            Alpha = Locked.Value ? 0.6f : 1;
        }
    }
}
