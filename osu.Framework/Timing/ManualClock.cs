// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

namespace osu.Framework.Timing
{
    /// <summary>
    /// A completely manual clock implementation. Everything is settable.
    /// </summary>
    public class ManualClock : IClock
    {
        public double CurrentTime { get; set; }
        public double Rate { get; set; } = 1.0;
        public bool IsRunning { get; set; }
    }

    /// <summary>
    /// A manual clock that also implements <see cref="IAdjustableClock"/>.
    /// </summary>
    public class ManualAdjustableClock : ManualClock, IAdjustableClock
    {
        public ManualAdjustableClock()
        {
            Rate = 1.0;
        }

        public void Reset()
        {
            CurrentTime = 0;
            IsRunning = false;
        }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public bool Seek(double position)
        {
            CurrentTime = position;
            return true;
        }

        public void ResetSpeedAdjustments() => Rate = 1.0;
    }
}
