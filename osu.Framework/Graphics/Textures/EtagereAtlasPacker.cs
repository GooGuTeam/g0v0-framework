// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
// Adapted from etagere, Copyright (c) 2020 Nicolas Silva. See LICENCE-ETAGERE.

using System.Collections.Generic;
using osu.Framework.Graphics.Primitives;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>
    /// Append-only adaptation of etagere's single-column shelf allocator.
    /// Without deallocation each shelf has only one remaining free interval.
    /// </summary>
    internal sealed class EtagereAtlasPacker : AppendOnlyAtlasPacker
    {
        private readonly List<Shelf> shelves = new List<Shelf>();
        private RectangleI bounds;

        protected override void Initialise(RectangleI area)
        {
            bounds = area;
            shelves.Clear();
            shelves.Add(new Shelf(area.X, area.Y, area.Height, true));
        }

        protected override bool Allocate(int width, int height, out Vector2I position)
        {
            int alignment = height < 32 ? 8 : height < 128 ? 16 : height < 512 ? 32 : 64;
            int alignedHeight = (height + alignment - 1) / alignment * alignment;
            if (alignedHeight <= bounds.Height)
                height = alignedHeight;

            int selected = -1;
            int selectedHeight = int.MaxValue;
            for (int i = 0; i < shelves.Count; i++)
            {
                Shelf shelf = shelves[i];
                if (shelf.Height < height || shelf.Height >= selectedHeight || bounds.Right - shelf.X < width)
                    continue;

                bool badFit = !shelf.Empty && shelf.Height > height + height / 2;
                if (badFit && selected >= 0)
                    continue;
                selected = i;
                if (!badFit)
                    selectedHeight = shelf.Height;
                if (shelf.Height == height)
                    break;
            }

            if (selected < 0)
            {
                position = default;
                return false;
            }

            Shelf chosen = shelves[selected];
            if (chosen.Empty && chosen.Height > height + 8)
            {
                shelves.Insert(selected + 1, new Shelf(bounds.X, chosen.Y + height, chosen.Height - height, true));
                chosen.Height = height;
            }
            position = new Vector2I(chosen.X, chosen.Y);
            chosen.X += width;
            if (bounds.Right - chosen.X <= 8)
                chosen.X = bounds.Right;
            chosen.Empty = false;
            shelves[selected] = chosen;
            return true;
        }

        private record struct Shelf(int X, int Y, int Height, bool Empty);
    }
}
