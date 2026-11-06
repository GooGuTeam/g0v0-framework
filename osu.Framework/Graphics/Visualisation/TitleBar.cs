// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;

namespace osu.Framework.Graphics.Visualisation
{
    internal partial class TitleBar : CompositeDrawable
    {
        private readonly ToolWindow window;

        public const float HEIGHT = 40;

        public TitleBar(ToolWindow window, string title, string keyHelpText)
        {
            this.window = window;

            RelativeSizeAxes = Axes.X;
            Size = new Vector2(1, HEIGHT);

            var collapseButton = new TitleBarButton(window, FontAwesome.Solid.AngleDoubleUp, window.ToggleCollapsed);

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = FrameworkColour.BlueDark,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Y,
                    AutoSizeAxes = Axes.X,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(10),
                    Children = new Drawable[]
                    {
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.Y,
                            AutoSizeAxes = Axes.X,
                            Direction = FillDirection.Horizontal,
                            Children = new Drawable[]
                            {
                                new TitleBarButton(window, FontAwesome.Solid.Times, window.Close),
                                collapseButton,
                            },
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = title,
                            Font = FrameworkFont.Condensed.With(weight: "Bold"),
                            Colour = FrameworkColour.Yellow,
                        },
                        new SpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Text = keyHelpText,
                            Font = FrameworkFont.Condensed,
                            Colour = FrameworkColour.Yellow,
                            Alpha = 0.5f
                        },
                    }
                },
            };

            // the button points up while the window is expanded (roll up), and down while it is rolled up (expand).
            window.Collapsed.BindValueChanged(collapsed => collapseButton.Icon = collapsed.NewValue ? FontAwesome.Solid.AngleDoubleDown : FontAwesome.Solid.AngleDoubleUp, true);
        }

        protected override bool OnDragStart(DragStartEvent e) => true;

        protected override void OnDrag(DragEvent e)
        {
            window.Position += e.Delta;
            base.OnDrag(e);
        }

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            window.BringToFront();
            return true;
        }

        private partial class TitleBarButton : CompositeDrawable
        {
            private readonly ToolWindow window;
            private readonly Action action;

            private readonly Box hoverLayer;
            private readonly SpriteIcon icon;

            public IconUsage Icon
            {
                get => icon.Icon;
                set => icon.Icon = value;
            }

            public TitleBarButton(ToolWindow window, IconUsage icon, Action action)
            {
                this.window = window;
                this.action = action;

                Size = new Vector2(HEIGHT);

                InternalChildren = new Drawable[]
                {
                    hoverLayer = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = FrameworkColour.Yellow,
                        Alpha = 0,
                    },
                    this.icon = new SpriteIcon
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(14),
                        Icon = icon,
                        Colour = FrameworkColour.Yellow,
                    },
                };
            }

            protected override bool OnHover(HoverEvent e)
            {
                hoverLayer.FadeTo(0.15f, 100, Easing.OutQuint);
                return true;
            }

            protected override void OnHoverLost(HoverLostEvent e) => hoverLayer.FadeTo(0, 100, Easing.OutQuint);

            // handle the press so that the title bar does not start dragging when the button is held down.
            protected override bool OnMouseDown(MouseDownEvent e)
            {
                window.BringToFront();
                return true;
            }

            protected override bool OnClick(ClickEvent e)
            {
                action();
                return true;
            }
        }
    }
}
