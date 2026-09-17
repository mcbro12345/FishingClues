using System;

namespace FishingClues;

public static class FishingDiscovery
{
    // 3704/3705/4502 are spoiler-hidden regions; having an ID in the agent's
    // array isn't proof of discovery, since locked entries have IDs too.
    public static bool IsRegionNameVisible(ushort placeNameId, bool hasDiscoveredHole, bool vanillaShowsName)
        => placeNameId switch
        {
            0 => hasDiscoveredHole,
            3704 or 3705 or 4502 => hasDiscoveredHole || vanillaShowsName,
            _ => true,
        };
    // RowId is the discovery key (Order is just a display-sort key), and rows
    // outside the ordinary notebook bitfield must not alias its bits.
    public static bool IsDiscovered(ReadOnlySpan<byte> flags, int bitCount, uint rowId)
    {
        if (bitCount <= 0 || rowId >= (uint)bitCount || rowId / 8 >= (uint)flags.Length)
            return false;
        return (flags[(int)(rowId / 8)] & (1 << (int)(rowId % 8))) != 0;
    }
}
