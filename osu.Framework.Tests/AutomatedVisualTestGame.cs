// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Testing;

namespace osu.Framework.Tests
{
    internal partial class AutomatedVisualTestGame : TestGame
    {
        public AutomatedVisualTestGame(string? testFilter = null)
        {
            var browser = new TestBrowser();
            browser.RunAllSteps.Value = true;

            if (!string.IsNullOrWhiteSpace(testFilter))
            {
                string[] filters = testFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                browser.TestTypes.RemoveAll(type => !filters.Any(filter => type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));

                if (browser.TestTypes.Count == 0)
                    throw new ArgumentException($"No visual tests match '{testFilter}'.", nameof(testFilter));
            }

            Add(new TestBrowserTestRunner(browser));
        }
    }
}
