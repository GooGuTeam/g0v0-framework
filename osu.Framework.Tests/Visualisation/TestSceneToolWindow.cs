// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Visualisation;
using osu.Framework.Testing;
using osu.Framework.Tests.Visual;
using osu.Framework.Utils;
using osuTK;

namespace osu.Framework.Tests.Visualisation
{
    [HeadlessTest]
    public partial class TestSceneToolWindow : FrameworkTestScene
    {
        [Test]
        public void TestWindowOrdering()
        {
            ToolWindowContainer container = null!;
            TestToolWindow first = null!;
            TestToolWindow second = null!;

            AddStep("add windows", () =>
            {
                Add(container = new ToolWindowContainer
                {
                    Children = new Drawable[]
                    {
                        first = new TestToolWindow("first"),
                        second = new TestToolWindow("second") { Position = new Vector2(20) },
                    }
                });
            });

            AddAssert("second window is in front", () => container.Children.Last() == second);

            AddStep("bring first window to front", () => first.BringToFront());
            AddAssert("first window is in front", () => container.Children.Last() == first);

            AddStep("show second window again", () =>
            {
                second.Hide();
                second.Show();
            });
            AddAssert("second window is in front", () => container.Children.Last() == second);
        }

        [Test]
        public void TestCollapse()
        {
            TestToolWindow window = null!;

            AddStep("add window", () => Add(window = new TestToolWindow("collapsible")));

            AddAssert("window is expanded", () => window.Collapsed.Value == false);
            AddAssert("window is full height", () => Precision.AlmostEquals(window.DrawHeight, ToolWindow.HEIGHT));

            AddStep("collapse window", () => window.ToggleCollapsed());
            AddAssert("window is collapsed", () => window.Collapsed.Value);
            AddUntilStep("only title bar remains", () => Precision.AlmostEquals(window.DrawHeight, TitleBar.HEIGHT));

            AddStep("expand window", () => window.ToggleCollapsed());
            AddUntilStep("window is full height", () => Precision.AlmostEquals(window.DrawHeight, ToolWindow.HEIGHT));
        }

        [Test]
        public void TestClose()
        {
            TestToolWindow window = null!;

            AddStep("add window", () => Add(window = new TestToolWindow("closable")));
            AddStep("close window", () => window.Close());

            AddAssert("window is hidden", () => window.State.Value, () => Is.EqualTo(Visibility.Hidden));
        }

        private partial class TestToolWindow : ToolWindow
        {
            public TestToolWindow(string title)
                : base(title, "(test)")
            {
            }
        }
    }
}
