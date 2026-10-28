// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;

namespace osu.Framework.Testing.Benchmarking.Display
{
    /// <summary>
    /// A rounded button with a text label.
    /// </summary>
    internal partial class DisplayButton : ClickableContainer
    {
        private readonly Box background;
        private readonly SpriteText label;

        private Colour4 colour = BenchmarkColours.PANEL_RAISED;

        public DisplayButton()
        {
            AutoSizeAxes = Axes.X;
            Height = 34;
            Masking = true;
            CornerRadius = 6;
            BorderColour = BenchmarkColours.BORDER;
            BorderThickness = 1;

            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both },
                label = new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Margin = new MarginPadding { Horizontal = 14 },
                    Font = BenchmarkColours.Font(15, bold: true),
                    Colour = BenchmarkColours.TEXT,
                },
            };
        }

        public string Text
        {
            set => label.Text = value;
        }

        public Colour4 BackgroundColour
        {
            set
            {
                colour = value;
                updateState();
            }
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Enabled.BindValueChanged(_ => updateState(), true);
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateState();
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            updateState();
            base.OnHoverLost(e);
        }

        private void updateState()
        {
            background.FadeColour(IsHovered && Enabled.Value ? colour.Lighten(0.15f) : colour, 100);
            this.FadeTo(Enabled.Value ? 1 : 0.4f, 100);
        }
    }
}
