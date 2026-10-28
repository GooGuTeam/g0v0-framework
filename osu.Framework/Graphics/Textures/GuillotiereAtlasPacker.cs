// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// Adapted from guillotiere, Copyright (c) 2019 Nicolas Silva. See LICENCE-GUILLOTIERE.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics.Primitives;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>Managed adaptation of guillotiere's append-only SimpleAtlasAllocator.</summary>
    internal sealed class GuillotiereAtlasPacker : AppendOnlyAtlasPacker
    {
        private readonly List<RectangleI>[] freeRectangles = { new List<RectangleI>(), new List<RectangleI>(), new List<RectangleI>() };

        protected override void Initialise(RectangleI bounds)
        {
            foreach (var bucket in freeRectangles)
                bucket.Clear();
            add(bounds);
        }

        protected override bool Allocate(int width, int height, out Vector2I position)
        {
            int idealBucket = bucketFor(width, height);
            bool worstFit = idealBucket == 2;
            for (int bucket = idealBucket; bucket < freeRectangles.Length; bucket++)
            {
                var rectangles = freeRectangles[bucket];
                int selected = -1;
                int score = worstFit ? 0 : int.MaxValue;
                for (int i = 0; i < rectangles.Count; i++)
                {
                    int dx = rectangles[i].Width - width;
                    int dy = rectangles[i].Height - height;
                    if (dx < 0 || dy < 0)
                        continue;
                    if (dx == 0 || dy == 0)
                    {
                        selected = i;
                        break;
                    }
                    int candidate = Math.Min(dx, dy);
                    if (worstFit ? candidate > score : candidate < score)
                    {
                        selected = i;
                        score = candidate;
                    }
                }

                if (selected < 0)
                    continue;
                RectangleI chosen = rectangles[selected];
                rectangles.RemoveAt(selected);
                int remainingWidth = chosen.Width - width;
                int remainingHeight = chosen.Height - height;
                if ((long)remainingWidth * height > (long)width * remainingHeight)
                {
                    add(new RectangleI(chosen.X + width, chosen.Y, remainingWidth, chosen.Height));
                    add(new RectangleI(chosen.X, chosen.Y + height, width, remainingHeight));
                }
                else
                {
                    add(new RectangleI(chosen.X, chosen.Y + height, chosen.Width, remainingHeight));
                    add(new RectangleI(chosen.X + width, chosen.Y, remainingWidth, height));
                }
                position = chosen.Location;
                return true;
            }
            position = default;
            return false;
        }

        private void add(RectangleI bounds)
        {
            if (bounds.Width > 0 && bounds.Height > 0)
                freeRectangles[bucketFor(bounds.Width, bounds.Height)].Add(bounds);
        }

        private static int bucketFor(int width, int height) => width >= 256 || height >= 256 ? 2 : width >= 32 || height >= 32 ? 1 : 0;
    }
}
