// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Platform;

namespace osu.Framework.Tests
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            bool benchmark = args.Contains(@"--benchmark");
            bool portable = args.Contains(@"--portable");
            string? testFilter = args.FirstOrDefault(arg => arg.StartsWith("--test-filter=", StringComparison.Ordinal))?.Substring("--test-filter=".Length);

            using (GameHost host = Host.GetSuitableDesktopHost(@"visual-tests", new HostOptions { PortableInstallation = portable }))
            {
                if (benchmark)
                    host.Run(new AutomatedVisualTestGame(testFilter));
                else
                    host.Run(new VisualTestGame());
            }
        }
    }
}
