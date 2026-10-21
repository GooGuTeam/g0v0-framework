// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using osu.Framework.Statistics;
using System.Collections.Generic;
using osu.Framework.Development;

namespace osu.Framework.Threading
{
    public class InputThread : GameThread
    {
        public InputThread()
            : base(name: "Input")
        {
        }

        internal override IEnumerable<StatisticsCounterType> StatisticsCounters => new[]
        {
            StatisticsCounterType.MouseEvents,
            StatisticsCounterType.KeyEvents,
            StatisticsCounterType.JoystickEvents,
            StatisticsCounterType.MidiEvents,
            StatisticsCounterType.TabletEvents,
            StatisticsCounterType.TouchEvents,
        };

        protected override void PrepareForWork()
        {
            // Intentionally inhibiting the base implementation which spawns a native thread.
            // Therefore, we need to run Initialize inline.
            Initialize(true);
        }

        public override bool IsCurrent => ThreadSafety.IsInputThread && (Thread == null || System.Threading.Thread.CurrentThread == Thread);

        internal sealed override void MakeCurrent()
        {
            base.MakeCurrent();

            ThreadSafety.IsInputThread = true;
        }
    }
}
