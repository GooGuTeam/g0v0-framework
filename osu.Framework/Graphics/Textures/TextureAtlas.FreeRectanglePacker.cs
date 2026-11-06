// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics.Primitives;

namespace osu.Framework.Graphics.Textures
{
    public partial class TextureAtlas
    {
        /// <summary>
        /// Incrementally packs unrotated allocations without moving previously assigned regions.
        /// All access is protected by the containing atlas' retrieval lock.
        /// </summary>
        internal sealed class FreeRectanglePacker : IAtlasPacker
        {
            // Free rectangles may overlap each other, but never an allocated footprint.
            // Keeping both possible split directions allows later allocations to span old row boundaries.
            private readonly List<RectangleI> freeRectangles = new List<RectangleI>();
            private readonly List<RectangleI> splitRectangles = new List<RectangleI>();

            public void Reset(int width, int height)
            {
                freeRectangles.Clear();
                splitRectangles.Clear();

                if (width <= PADDING || height <= PADDING)
                    return;

                // Each footprint includes trailing padding; the usable area's origin supplies leading padding.
                freeRectangles.Add(new RectangleI(PADDING, PADDING, width - PADDING, height - PADDING));
                reserve(new RectangleI(0, 0, WHITE_PIXEL_SIZE + PADDING, WHITE_PIXEL_SIZE + PADDING));
            }

            public bool TryAllocate(int width, int height, out Vector2I position)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(width);
                ArgumentOutOfRangeException.ThrowIfNegative(height);

                // Zero-sized glyphs still reserve padding, matching the existing atlas' allocation contract.
                int paddedWidth = checked(width + PADDING);
                int paddedHeight = checked(height + PADDING);
                int bestIndex = -1;
                int bestShortSide = int.MaxValue;
                int bestLongSide = int.MaxValue;

                for (int i = 0; i < freeRectangles.Count; i++)
                {
                    RectangleI free = freeRectangles[i];
                    int remainingWidth = free.Width - paddedWidth;
                    int remainingHeight = free.Height - paddedHeight;

                    if (remainingWidth < 0 || remainingHeight < 0)
                        continue;

                    int shortSide = Math.Min(remainingWidth, remainingHeight);
                    int longSide = Math.Max(remainingWidth, remainingHeight);

                    if (bestIndex >= 0 && (shortSide > bestShortSide || (shortSide == bestShortSide && longSide >= bestLongSide)))
                        continue;

                    bestIndex = i;
                    bestShortSide = shortSide;
                    bestLongSide = longSide;
                }

                if (bestIndex < 0)
                {
                    position = default;
                    return false;
                }

                position = freeRectangles[bestIndex].Location;
                reserve(new RectangleI(position.X, position.Y, paddedWidth, paddedHeight));
                return true;
            }

            private void reserve(RectangleI allocation)
            {
                // Collect fragments separately so they cannot affect traversal of the original free rectangles.
                for (int i = freeRectangles.Count - 1; i >= 0; i--)
                {
                    RectangleI free = freeRectangles[i];
                    if (!free.IntersectsWith(allocation))
                        continue;

                    freeRectangles.RemoveAt(i);

                    if (allocation.Left > free.Left)
                        addFreeRectangle(new RectangleI(free.Left, free.Top, allocation.Left - free.Left, free.Height));

                    if (allocation.Right < free.Right)
                        addFreeRectangle(new RectangleI(allocation.Right, free.Top, free.Right - allocation.Right, free.Height));

                    if (allocation.Top > free.Top)
                        addFreeRectangle(new RectangleI(free.Left, free.Top, free.Width, allocation.Top - free.Top));

                    if (allocation.Bottom < free.Bottom)
                        addFreeRectangle(new RectangleI(free.Left, allocation.Bottom, free.Width, free.Bottom - allocation.Bottom));
                }

                // Unchanged entries were already pruned. Only new fragments need containment comparisons.
                foreach (RectangleI fragment in splitRectangles)
                {
                    bool contained = false;

                    for (int i = 0; i < freeRectangles.Count; i++)
                    {
                        if (freeRectangles[i].Contains(fragment))
                        {
                            contained = true;
                            break;
                        }

                        if (fragment.Contains(freeRectangles[i]))
                            freeRectangles.RemoveAt(i--);
                    }

                    if (!contained)
                        freeRectangles.Add(fragment);
                }

                splitRectangles.Clear();
            }

            private void addFreeRectangle(RectangleI rectangle)
            {
                // Even an empty glyph needs a padded footprint, so thinner fragments cannot serve future allocations.
                if (rectangle.Width >= PADDING && rectangle.Height >= PADDING)
                    splitRectangles.Add(rectangle);
            }
        }
    }
}
