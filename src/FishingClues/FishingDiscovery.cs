using System;

namespace FishingClues;

public static class FishingDiscovery
{
    // These journal regions have spoiler-hidden labels. Presence in the agent's
    // region-ID array is not a discovery signal: locked entries also have IDs.
    public static bool IsRegionNameVisible(ushort placeNameId, bool hasDiscoveredHole, bool vanillaShowsName)
        => placeNameId switch
        {
            0 => hasDiscoveredHole,
            3704 or 3705 or 4502 => hasDiscoveredHole || vanillaShowsName,
            _ => true,
        };
    // FishingSpot.RowId is the discovery key. Order is only a display-sort key.
    // Special rows outside the ordinary notebook bitfield must not alias its bits.
    public static bool IsDiscovered(ReadOnlySpan<byte> flags, int bitCount, uint rowId)
    {
        if (bitCount <= 0 || rowId >= (uint)bitCount || rowId / 8 >= (uint)flags.Length)
            return false;
        return (flags[(int)(rowId / 8)] & (1 << (int)(rowId % 8))) != 0;
    }
}
