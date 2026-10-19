// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osuTK;

namespace osu.Framework.Input.Handlers.Tablet
{
    /// <summary>
    /// Report-time position smoothing and stable-state button debouncing.
    /// The caller serialises reports, settings changes and input collection.
    /// </summary>
    internal sealed class TabletInputFilter
    {
        private Vector2? position;
        private double lastTime;
        private readonly Dictionary<int, ButtonState> buttons = new Dictionary<int, ButtonState>();

        public Vector2 FilterPosition(Vector2 input, double now, double smoothing, double radialStrength, Vector2 millimetresPerPixel)
        {
            double elapsed = now - lastTime;
            if (position == null || (radialStrength <= 0 && smoothing <= 0) || elapsed > 100 || elapsed < 0)
                position = input;
            else if (radialStrength > 0)
            {
                Vector2 delta = input - position.Value;
                double x = delta.X * (double)millimetresPerPixel.X;
                double y = delta.Y * (double)millimetresPerPixel.Y;
                double distance = Math.Sqrt(x * x + y * y);
                if (distance > 0)
                {
                    double remaining = RadialFollowSmoothing.RemainingDistance(distance, elapsed, radialStrength);
                    position += delta * (float)(1 - remaining / distance);
                }
            }
            else
                position += (input - position.Value) * (float)(1 - Math.Exp(-elapsed / smoothing));
            lastTime = now;
            return position.Value;
        }

        public void SetButton(int key, bool pressed, double now)
        {
            if (!buttons.TryGetValue(key, out var state))
                buttons.Add(key, state = new ButtonState());
            if (state.Candidate != pressed)
            {
                state.Candidate = pressed;
                state.Since = now;
            }
        }

        public void Flush(double now, double debounce, Action<int, bool> emit)
        {
            foreach (var pair in buttons)
            {
                var state = pair.Value;
                if (state.Candidate == state.Pressed || now - state.Since < debounce)
                    continue;
                state.Pressed = state.Candidate;
                emit(pair.Key, state.Pressed);
            }
        }

        public void ResetPosition() => position = null;

        public void Reset(Action<int, bool> emit)
        {
            ResetPosition();
            foreach (var pair in buttons)
            {
                if (pair.Value.Pressed)
                    emit(pair.Key, false);
            }
            buttons.Clear();
        }

        private sealed class ButtonState
        {
            public bool Pressed;
            public bool Candidate;
            public double Since;
        }
    }
}
