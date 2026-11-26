// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// Horizontal pair kerning read from an OpenType <c>GPOS</c> table (the <c>kern</c> feature's pair adjustment lookups).
    /// </summary>
    /// <remarks>
    /// FreeType's own kerning API only reads the legacy <c>kern</c> table, which most current fonts no longer ship.
    /// Only the X advance of the first glyph is applied, which is what horizontal left-to-right text needs;
    /// scripts, languages and contextual lookups are not evaluated.
    /// </remarks>
    internal sealed class OpenTypeKerning
    {
        private const ushort lookup_type_pair = 2;
        private const ushort lookup_type_extension = 9;
        private const ushort value_x_advance = 0x0004;

        private readonly byte[] data;

        /// <summary>
        /// Absolute offsets of the pair adjustment subtables, grouped by lookup in lookup order.
        /// </summary>
        private readonly List<int[]> lookups;

        private OpenTypeKerning(byte[] data, List<int[]> lookups)
        {
            this.data = data;
            this.lookups = lookups;
        }

        /// <summary>
        /// Reads the kerning lookups of a font file, or returns <c>null</c> if it has no (readable) <c>GPOS</c> kerning.
        /// </summary>
        public static OpenTypeKerning? TryCreate(byte[] font)
        {
            try
            {
                int gpos = findTable(font, "GPOS");
                if (gpos < 0)
                    return null;

                int featureList = gpos + u16(font, gpos + 6);
                int lookupList = gpos + u16(font, gpos + 8);

                var lookupIndices = new SortedSet<int>();
                int featureCount = u16(font, featureList);

                for (int i = 0; i < featureCount; i++)
                {
                    int record = featureList + 2 + i * 6;
                    if (font[record] != 'k' || font[record + 1] != 'e' || font[record + 2] != 'r' || font[record + 3] != 'n')
                        continue;

                    int feature = featureList + u16(font, record + 4);
                    int count = u16(font, feature + 2);
                    for (int j = 0; j < count; j++)
                        lookupIndices.Add(u16(font, feature + 4 + j * 2));
                }

                var lookups = new List<int[]>();

                foreach (int index in lookupIndices)
                {
                    int lookup = lookupList + u16(font, lookupList + 2 + index * 2);
                    ushort type = u16(font, lookup);
                    int subtableCount = u16(font, lookup + 4);
                    var subtables = new List<int>();

                    for (int j = 0; j < subtableCount; j++)
                    {
                        int subtable = lookup + u16(font, lookup + 6 + j * 2);
                        ushort subtableType = type;

                        if (type == lookup_type_extension)
                        {
                            subtableType = u16(font, subtable + 2);
                            subtable += (int)u32(font, subtable + 4);
                        }

                        if (subtableType == lookup_type_pair)
                            subtables.Add(subtable);
                    }

                    if (subtables.Count > 0)
                        lookups.Add(subtables.ToArray());
                }

                return lookups.Count > 0 ? new OpenTypeKerning(font, lookups) : null;
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                // A malformed table must not prevent the font from loading; it is simply not kerned.
                return null;
            }
        }

        /// <summary>
        /// The X advance adjustment between two glyphs, in font units.
        /// </summary>
        public int Get(uint left, uint right)
        {
            int total = 0;

            try
            {
                foreach (int[] subtables in lookups)
                {
                    foreach (int subtable in subtables)
                    {
                        if (tryPair(subtable, left, right, out int value))
                        {
                            total += value;
                            break;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                return total;
            }

            return total;
        }

        private bool tryPair(int subtable, uint left, uint right, out int value)
        {
            value = 0;

            ushort format = u16(data, subtable);
            int coverageIndex = coverage(subtable + u16(data, subtable + 2), left);
            if (coverageIndex < 0)
                return false;

            ushort valueFormat1 = u16(data, subtable + 4);
            ushort valueFormat2 = u16(data, subtable + 6);
            int size1 = valueRecordSize(valueFormat1);
            int size2 = valueRecordSize(valueFormat2);

            switch (format)
            {
                case 1:
                {
                    int pairSetCount = u16(data, subtable + 8);
                    if (coverageIndex >= pairSetCount)
                        return false;

                    int pairSet = subtable + u16(data, subtable + 10 + coverageIndex * 2);
                    int count = u16(data, pairSet);
                    int recordSize = 2 + size1 + size2;

                    // Pair value records are sorted by second glyph.
                    int lo = 0, hi = count - 1;

                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        int record = pairSet + 2 + mid * recordSize;
                        ushort second = u16(data, record);

                        if (second == right)
                        {
                            value = xAdvance(record + 2, valueFormat1);
                            return true;
                        }

                        if (second < right)
                            lo = mid + 1;
                        else
                            hi = mid - 1;
                    }

                    return false;
                }

                case 2:
                {
                    int class1 = classOf(subtable + u16(data, subtable + 8), left);
                    int class2 = classOf(subtable + u16(data, subtable + 10), right);
                    int class1Count = u16(data, subtable + 12);
                    int class2Count = u16(data, subtable + 14);

                    if (class1 >= class1Count || class2 >= class2Count)
                        return false;

                    int record = subtable + 16 + (class1 * class2Count + class2) * (size1 + size2);
                    value = xAdvance(record, valueFormat1);

                    // A covered first glyph consumes the pair even when the class pair has no adjustment.
                    return true;
                }

                default:
                    return false;
            }
        }

        private int xAdvance(int valueRecord, ushort valueFormat)
        {
            if ((valueFormat & value_x_advance) == 0)
                return 0;

            // XAdvance follows XPlacement and YPlacement, when present.
            int offset = 0;
            if ((valueFormat & 0x0001) != 0) offset += 2;
            if ((valueFormat & 0x0002) != 0) offset += 2;

            return (short)u16(data, valueRecord + offset);
        }

        private static int valueRecordSize(ushort valueFormat)
        {
            int bits = 0;
            for (int f = valueFormat & 0xFF; f != 0; f >>= 1)
                bits += f & 1;
            return bits * 2;
        }

        private int coverage(int table, uint glyph)
        {
            ushort format = u16(data, table);
            int count = u16(data, table + 2);
            int lo = 0, hi = count - 1;

            switch (format)
            {
                case 1:
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        ushort g = u16(data, table + 4 + mid * 2);

                        if (g == glyph)
                            return mid;

                        if (g < glyph)
                            lo = mid + 1;
                        else
                            hi = mid - 1;
                    }

                    return -1;

                case 2:
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        int range = table + 4 + mid * 6;
                        ushort start = u16(data, range);
                        ushort end = u16(data, range + 2);

                        if (glyph < start)
                            hi = mid - 1;
                        else if (glyph > end)
                            lo = mid + 1;
                        else
                            return u16(data, range + 4) + (int)(glyph - start);
                    }

                    return -1;

                default:
                    return -1;
            }
        }

        private int classOf(int table, uint glyph)
        {
            ushort format = u16(data, table);

            switch (format)
            {
                case 1:
                {
                    ushort start = u16(data, table + 2);
                    int count = u16(data, table + 4);
                    return glyph >= start && glyph < start + count ? u16(data, table + 6 + (int)(glyph - start) * 2) : 0;
                }

                case 2:
                {
                    int count = u16(data, table + 2);
                    int lo = 0, hi = count - 1;

                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        int range = table + 4 + mid * 6;

                        if (glyph < u16(data, range))
                            hi = mid - 1;
                        else if (glyph > u16(data, range + 2))
                            lo = mid + 1;
                        else
                            return u16(data, range + 4);
                    }

                    return 0;
                }

                default:
                    return 0;
            }
        }

        private static int findTable(byte[] font, string tag)
        {
            int numTables = u16(font, 4);

            for (int i = 0; i < numTables; i++)
            {
                int record = 12 + i * 16;
                if (font[record] == tag[0] && font[record + 1] == tag[1] && font[record + 2] == tag[2] && font[record + 3] == tag[3])
                    return (int)u32(font, record + 8);
            }

            return -1;
        }

        private static ushort u16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

        private static uint u32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }
}
