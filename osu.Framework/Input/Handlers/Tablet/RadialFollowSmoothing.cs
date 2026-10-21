// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Input.Handlers.Tablet
{
    /// <summary>
    /// Independent implementation of the mathematical response described by
    /// AbstractQbit's RadialFollowGraphs.ggb. Mapping and strength policy are local.
    /// </summary>
    internal static class RadialFollowSmoothing
    {
        private const double knee_scale = 0.3;
        private const double leak = 0.02;
        private const double reference_interval = 4;

        // At this knee scale the root lies on the linear branch: k * (x / k) + 1 = 0.
        // Its derivative is one, so the graph's derivative compensation is also one.
        private const double knee_root = -1;

        public static double RemainingDistance(double distance, double elapsedMilliseconds, double strength)
        {
            if (!double.IsFinite(distance) || distance <= 0 || !double.IsFinite(elapsedMilliseconds)
                || !double.IsFinite(strength) || strength <= 0)
                return 0;

            double amount = Math.Clamp(strength, 0, 1);
            double inner = 0.2 * amount;
            double outer = 0.75 * amount;
            if (distance <= inner)
                return distance;

            double width = outer - inner;
            // The graph has no time unit. A local 250 Hz reference preserves its 0.9
            // retention at 4 ms and normalises the linear response at other report rates.
            double retention = Math.Pow(0.9, Math.Max(0, elapsedMilliseconds) / reference_interval);
            double normalised = (distance - inner) * retention / width;
            double knee = knee_scale * kneeFunction((normalised + knee_root) / knee_scale) + 1;
            double remaining = inner + width * ((1 - leak) * knee + leak * normalised);
            return Math.Clamp(remaining, inner, distance);
        }

        private static double kneeFunction(double value)
        {
            if (value < -3)
                return value;
            if (value >= 3)
                return 0;
            return Math.Log(Math.Tanh(Math.Exp(value)));
        }
    }
}
