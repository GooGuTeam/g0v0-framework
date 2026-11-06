// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osuTK;

namespace osu.Framework.Graphics.Visualisation
{
    internal abstract partial class ToolWindow : OverlayContainer
    {
        public const float WIDTH = 500;
        public const float HEIGHT = 600;

        private const float button_width = 140;
        private const float button_height = 40;

        private const double collapse_duration = 200;

        /// <summary>
        /// Whether this window is rolled up, in which case only its title bar remains visible.
        /// </summary>
        public readonly BindableBool Collapsed = new BindableBool();

        protected readonly FillFlowContainer ToolbarContent;

        protected readonly FillFlowContainer MainHorizontalContent;

        protected ScrollContainer<Drawable> ScrollContent;

        protected readonly SearchContainer SearchContainer;

        protected ToolWindow(string title, string keyHelpText, bool supportsSearch = false)
        {
            AutoSizeAxes = Axes.X;
            Height = HEIGHT;

            Masking = true; // for cursor masking

            BasicTextBox queryTextBox;

            AddRangeInternal(new Drawable[]
            {
                new Box
                {
                    Colour = FrameworkColour.GreenDark,
                    RelativeSizeAxes = Axes.Both,
                    Depth = 0
                },
                new GridContainer
                {
                    RelativeSizeAxes = Axes.Y,
                    AutoSizeAxes = Axes.X,
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.AutoSize),
                        new Dimension(GridSizeMode.Distributed),
                    },
                    ColumnDimensions = new[]
                    {
                        new Dimension(GridSizeMode.AutoSize),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Children = new Drawable[]
                                {
                                    new TitleBar(this, title, keyHelpText),
                                    new Container //toolbar
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Children = new Drawable[]
                                        {
                                            new Box
                                            {
                                                Colour = FrameworkColour.BlueGreenDark,
                                                RelativeSizeAxes = Axes.Both,
                                            },
                                            ToolbarContent = new FillFlowContainer
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                AutoSizeAxes = Axes.Y,
                                                Spacing = new Vector2(5),
                                                Padding = new MarginPadding(5),
                                            },
                                        },
                                    }
                                }
                            },
                        },
                        new Drawable[]
                        {
                            new TooltipContainer
                            {
                                RelativeSizeAxes = Axes.Y,
                                AutoSizeAxes = Axes.X,
                                Child = MainHorizontalContent = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.Y,
                                    AutoSizeAxes = Axes.X,
                                    Direction = FillDirection.Horizontal,
                                    Child = new GridContainer
                                    {
                                        RelativeSizeAxes = Axes.Y,
                                        Width = WIDTH,
                                        RowDimensions = new[]
                                        {
                                            new Dimension(GridSizeMode.AutoSize),
                                            new Dimension(GridSizeMode.Distributed)
                                        },
                                        Content = new[]
                                        {
                                            new Drawable[]
                                            {
                                                queryTextBox = new BasicTextBox
                                                {
                                                    Width = WIDTH,
                                                    Height = 30,
                                                    PlaceholderText = "Search...",
                                                    Alpha = supportsSearch ? 1 : 0,
                                                }
                                            },
                                            new Drawable[]
                                            {
                                                ScrollContent = new BasicScrollContainer<Drawable>
                                                {
                                                    RelativeSizeAxes = Axes.Both,
                                                    ScrollbarOverlapsContent = false,
                                                    Child = SearchContainer = new SearchContainer
                                                    {
                                                        AutoSizeAxes = Axes.Y,
                                                        RelativeSizeAxes = Axes.X,
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            },
                        }
                    },
                },
                new CursorContainer()
            });

            queryTextBox.Current.BindValueChanged(term => SearchContainer.SearchTerm = term.NewValue, true);

            // rolling the window up leaves only the title bar visible, as the remaining content is masked away.
            Collapsed.BindValueChanged(collapsed => this.ResizeHeightTo(collapsed.NewValue ? TitleBar.HEIGHT : HEIGHT, collapse_duration, Easing.OutQuint));
        }

        protected void AddButton(string text, Action action)
        {
            ToolbarContent.Add(new BasicButton
            {
                Size = new Vector2(button_width, button_height),
                Text = text,
                Action = action
            });
        }

        protected void AddButton(IconUsage icon, Action action)
        {
            ToolbarContent.Add(new IconButton(icon)
            {
                Action = action
            });
        }

        /// <summary>
        /// Rolls this window up, or expands it again if it is already rolled up.
        /// </summary>
        public void ToggleCollapsed() => Collapsed.Value = !Collapsed.Value;

        /// <summary>
        /// Moves this window to the front of the layer hosting it.
        /// </summary>
        public void BringToFront()
        {
            for (CompositeDrawable? ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is ToolWindowContainer container)
                {
                    container.BringToFront(this);
                    return;
                }
            }
        }

        /// <summary>
        /// Hides this window.
        /// </summary>
        /// <remarks>
        /// The window is not disposed and can still be shown again, usually via its global key binding.
        /// </remarks>
        public virtual void Close() => Hide();

        protected override bool OnMouseDown(MouseDownEvent e)
        {
            // interacting with a window should raise it above any others it overlaps with.
            BringToFront();
            return base.OnMouseDown(e);
        }

        protected override void PopIn() => this.FadeIn(100);

        protected override void PopOut() => this.FadeOut(100);

        private partial class IconButton : BasicButton
        {
            public IconButton(IconUsage icon)
            {
                AutoSizeAxes = Axes.X;
                Height = button_height;

                Add(new SpriteIcon
                {
                    Size = new Vector2(16),
                    Margin = new MarginPadding { Horizontal = 10 },
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Icon = icon,
                    Colour = FrameworkColour.Yellow,
                });
            }
        }
    }
}
