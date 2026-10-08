// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osu.Framework.Utils;
using osuTK;

namespace osu.Framework.Tests.Visual.Drawables
{
    /// <summary>
    /// Covers what <see cref="LoadErrorHandling"/> actually puts on screen: a component which throws while loading is replaced by a
    /// <see cref="LoadErrorPlaceholder"/> laid out and drawn in the slot the failed component occupied, while its siblings are left alone.
    /// </summary>
    public partial class TestSceneLoadErrorPlaceholder : FrameworkTestScene
    {
        private static readonly Vector2 failed_size = new Vector2(240, 120);
        private static readonly Colour4 healthy_colour = Colour4.FromHex("#4CAF50");

        // note that [SetUp]/[TearDown] would be the wrong hooks here: a TestScene only queues its steps during the test method, and runs them
        // later (from the runner, after [TearDown] has already fired). set-up/tear-down work therefore has to be expressed as steps.
        //
        // LoadErrorHandling being a global means its state leaks between tests (and into other fixtures). it is therefore also reset on the way
        // in: a test which aborts on an unexpected exception never reaches its tear-down steps, so cleaning up only on the way out would leave a
        // custom placeholder factory installed for everything that runs afterwards.
        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset placeholder factory", () => LoadErrorHandling.PlaceholderFactory = null);
            AddStep("enable load error handling", () => LoadErrorHandling.Enabled = true);
        }

        [TearDownSteps]
        public void TearDownSteps()
        {
            AddStep("disable load error handling", () => LoadErrorHandling.Enabled = false);
            AddStep("reset placeholder factory", () => LoadErrorHandling.PlaceholderFactory = null);
        }

        [Test]
        public void TestFixedSizeChildIsReplacedInPlace()
        {
            IsolationRow row = null;

            AddStep("create row", () => Child = row = new IsolationRow(failed_size));

            // note the >= : by the time an assertion runs, the scene has already updated for a few frames, so the siblings have progressed
            // past LoadState.Ready into LoadState.Loaded.
            AddAssert("siblings unaffected", () => row.HealthyBefore.LoadState >= LoadState.Ready
                                                   && row.HealthyAfter.LoadState >= LoadState.Ready
                                                   && row.HealthyBefore.Parent == row
                                                   && row.HealthyAfter.Parent == row);

            AddAssert("failed child discarded", () => row.Failing.Parent == null
                                                      && row.Failing.LoadState < LoadState.Ready
                                                      && !row.Children.Contains(row.Failing));

            AddAssert("one placeholder shown", () => row.ChildrenOfType<LoadErrorPlaceholder>().Count() == 1);

            // the point of the feature: the failure is reported where the component used to be, not appended to the end of the container.
            AddAssert("placeholder occupies the failed child's slot", () => placeholderIn(row) == row.Children[1]);

            AddAssert("placeholder inherits the failed child's size", () =>
            {
                LoadErrorPlaceholder placeholder = placeholderIn(row);

                return placeholder.RelativeSizeAxes == Axes.None && Precision.AlmostEquals(placeholder.DrawSize, failed_size);
            });

            AddAssert("placeholder is drawn between its siblings", () =>
            {
                LoadErrorPlaceholder placeholder = placeholderIn(row);

                // screen-space bounds are what actually gets drawn, so this is the closest a headless run gets to "it looks right".
                return placeholder.ScreenSpaceDrawQuad.AABBFloat.Left >= row.HealthyBefore.ScreenSpaceDrawQuad.AABBFloat.Right
                       && placeholder.ScreenSpaceDrawQuad.AABBFloat.Right <= row.HealthyAfter.ScreenSpaceDrawQuad.AABBFloat.Left;
            });

            AddAssert("placeholder describes the failure", () =>
            {
                LoadErrorPlaceholder placeholder = placeholderIn(row);

                return placeholder.FailedDrawable == row.Failing && placeholder.Failure is IntentionalLoadException;
            });

            AddAssert("placeholder is drawn at all", () =>
            {
                LoadErrorPlaceholder placeholder = placeholderIn(row);

                // DrawSize / ScreenSpaceDrawQuad are resolved during update, so these hold even though nothing is rasterised headlessly.
                return placeholder.ScreenSpaceDrawQuad.Width > 0 && placeholder.ScreenSpaceDrawQuad.Height > 0;
            });

            AddAssert("placeholder explains the failure on screen", () =>
            {
                // the whole point of a placeholder is that it says what broke, so the text it would render is part of the contract.
                string[] lines = [.. placeholderIn(row).ChildrenOfType<SpriteText>().Select(text => text.Text.ToString())];

                return lines.Any(line => line.Contains("failed to load"))
                       && lines.Any(line => line.Contains(nameof(IntentionalLoadException)));
            });

            AddAssert("placeholder is tinted to stand out", () =>
            {
                Box tint = placeholderIn(row).ChildrenOfType<Box>().Single();
                Colour4 colour = tint.Colour;

                return tint.RelativeSizeAxes == Axes.Both && colour == Colour4.Orange && tint.Alpha == 0.35f;
            });
        }

        [Test]
        public void TestRelativeSizedChildIsReplacedInPlace()
        {
            Container row = null;

            AddStep("create relatively sized row", () => Child = row = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = failed_size,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = healthy_colour },
                    new FailingBox { RelativeSizeAxes = Axes.Both },
                },
            });

            AddAssert("placeholder fills the same area", () =>
            {
                LoadErrorPlaceholder placeholder = placeholderIn(row);

                return placeholder.RelativeSizeAxes == Axes.Both && Precision.AlmostEquals(placeholder.DrawSize, row.ChildSize);
            });
        }

        [Test]
        public void TestAsyncFailureIsReplacedInPlace()
        {
            DelayedLoadWrapper wrapper = null;

            AddStep("create delayed load wrapper", () => Child = new Container
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Both,
                Child = wrapper = new DelayedLoadWrapper(new FailingBox(), 0)
                {
                    Size = failed_size,
                },
            });

            // the asynchronous path cannot replace the component in-place, so it reports the failure instead and the wrapper consumes it.
            AddUntilStep("placeholder shown", () => wrapper.ChildrenOfType<LoadErrorPlaceholder>().Any());

            AddAssert("wrapper reports the load as completed", () => wrapper.DelayedLoadCompleted);

            AddAssert("placeholder is drawn in the wrapper", () =>
            {
                LoadErrorPlaceholder placeholder = placeholderIn(wrapper);

                return placeholder.Parent == wrapper && placeholder.ScreenSpaceDrawQuad.Width > 0;
            });
        }

        [Test]
        public void TestCustomPlaceholderFactoryIsUsed()
        {
            AddStep("register custom placeholder", () => LoadErrorHandling.PlaceholderFactory = (_, _) => new CustomPlaceholder());

            AddStep("create row", () => Child = new IsolationRow(failed_size));

            AddAssert("custom placeholder used", () => this.ChildrenOfType<CustomPlaceholder>().Count() == 1);

            AddAssert("custom placeholder occupies the failed component's slot", () =>
            {
                CustomPlaceholder placeholder = this.ChildrenOfType<CustomPlaceholder>().Single();

                // a placeholder controls how it looks, but not where it goes - it shares the slot contract with the parent's layout, so the
                // failed component's geometry is imposed even on a custom placeholder.
                return Precision.AlmostEquals(placeholder.Size, failed_size) && placeholder.RelativeSizeAxes == Axes.None;
            });

            AddAssert("custom placeholder is placed between its siblings", () =>
            {
                CustomPlaceholder placeholder = this.ChildrenOfType<CustomPlaceholder>().Single();

                return placeholder.ChildrenOfType<Box>().Single().Colour == Colour4.Yellow
                       && placeholder.ScreenSpaceDrawQuad.Width > 0;
            });
        }

        [Test]
        public void TestPlaceholderAnchorCannotBreakFlowingParent()
        {
            IsolationRow row = null;

            // a placeholder which anchors itself differently to its siblings makes a flowing parent throw on its next layout pass, so the
            // framework has to impose the failed component's anchor rather than trusting the placeholder to pick a compatible one.
            AddStep("register a differently anchored placeholder", () => LoadErrorHandling.PlaceholderFactory = (_, _) => new WronglyAnchoredPlaceholder());

            AddStep("create row", () => Child = row = new IsolationRow(failed_size));

            AddAssert("all children agree on an anchor", () =>
                row.Children.Select(child => child.RelativeAnchorPosition).Distinct().Count() == 1);

            AddAssert("placeholder is still laid out and drawn", () =>
            {
                WronglyAnchoredPlaceholder placeholder = row.ChildrenOfType<WronglyAnchoredPlaceholder>().Single();

                return placeholder.ScreenSpaceDrawQuad.Width > 0 && placeholder.ScreenSpaceDrawQuad.Height > 0;
            });
        }

        [Test]
        public void TestComponentWhichMisconfiguresItselfWhileCompletingItsLoadIsReplaced()
        {
            SidePanel panel = null;

            AddStep("create panel", () => Child = panel = new SidePanel());

            AddUntilStep("placeholder shown", () => panel.ChildrenOfType<LoadErrorPlaceholder>().Any());

            AddAssert("placeholder occupies the failed component's slot", () => panel.Children[0] == placeholderIn(panel));

            AddAssert("placeholder is drawn rather than inheriting a slot it cannot be seen in", () =>
            {
                LoadErrorPlaceholder placeholder = placeholderIn(panel);

                // the failed component was sized relatively along an axis it never claimed any extent on, and its parent sizes itself along that
                // same axis - so honouring its layout literally would leave the placeholder with nothing to draw into, and the failure would look
                // indistinguishable from a component which was simply never added.
                return placeholder.ScreenSpaceDrawQuad.Width > 0 && placeholder.ScreenSpaceDrawQuad.Height > 0;
            });
        }

        private static LoadErrorPlaceholder placeholderIn(Drawable target) => target.ChildrenOfType<LoadErrorPlaceholder>().Single();

        /// <summary>
        /// A row of three fixed-size components, the middle of which throws while loading.
        /// </summary>
        private partial class IsolationRow : FillFlowContainer
        {
            public readonly Box HealthyBefore;
            public readonly FailingBox Failing;
            public readonly Box HealthyAfter;

            public IsolationRow(Vector2 size)
            {
                Anchor = Anchor.Centre;
                Origin = Anchor.Centre;
                Direction = FillDirection.Horizontal;
                Spacing = new Vector2(10);
                AutoSizeAxes = Axes.Both;

                InternalChildren = new Drawable[]
                {
                    HealthyBefore = new Box { Size = size, Colour = healthy_colour },
                    Failing = new FailingBox { Size = size },
                    HealthyAfter = new Box { Size = size, Colour = healthy_colour },
                };
            }
        }

        /// <summary>
        /// A component which always throws from its dependency loader.
        /// </summary>
        public partial class FailingBox : Box
        {
            [BackgroundDependencyLoader]
            private void load() => throw new IntentionalLoadException();
        }

        /// <summary>
        /// A stand-in for a game-provided placeholder.
        /// </summary>
        private partial class CustomPlaceholder : CompositeDrawable
        {
            public CustomPlaceholder()
            {
                AutoSizeAxes = Axes.Both;

                InternalChildren = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Yellow },
                };
            }
        }

        /// <summary>
        /// A placeholder which anchors itself to the centre of its parent, unlike its siblings.
        /// </summary>
        private partial class WronglyAnchoredPlaceholder : CompositeDrawable
        {
            public WronglyAnchoredPlaceholder()
            {
                AutoSizeAxes = Axes.Both;
                Anchor = Anchor.Centre;
                Origin = Anchor.Centre;

                InternalChildren = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Yellow },
                };
            }
        }

        /// <summary>
        /// A panel shaped like the one this isolation was added for: a component which is handed a relative size, and only discovers - once it is
        /// loaded - that it also sizes itself along that same axis. Its parent sizes itself along that axis too, so the two compound: the panel has
        /// no height of its own, and the component inherits none from it either.
        /// </summary>
        private partial class SidePanel : Container
        {
            [BackgroundDependencyLoader]
            private void load()
            {
                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
                Padding = new MarginPadding(20);

                Child = new SelfMisconfiguringBanner();
            }
        }

        /// <summary>
        /// A component which contradicts the layout it was given the moment it is loaded, by sizing itself along an axis it is already sized
        /// relatively along - which <see cref="CompositeDrawable.AutoSizeAxes"/> rejects with an <see cref="InvalidOperationException"/>.
        /// This is the shape of a component which resizes itself to fit the first value it is given (as osu!'s settings notes do).
        /// </summary>
        public partial class SelfMisconfiguringBanner : CompositeDrawable
        {
            public SelfMisconfiguringBanner()
            {
                RelativeSizeAxes = Axes.Y;
            }

            protected override void LoadComplete() => AutoSizeAxes = Axes.Y;
        }

        public class IntentionalLoadException : Exception
        {
            public IntentionalLoadException()
                : base("This component fails to load on purpose.")
            {
            }
        }
    }
}
