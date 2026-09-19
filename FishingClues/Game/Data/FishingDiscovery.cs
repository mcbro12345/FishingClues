using System;

namespace FishingClues.Game.Data;

public static class FishingDiscovery
{
    // Region place names the vanilla Fishing Log hides until they are discovered.
    public static readonly ushort[] HiddenRegionPlaceNameIds = [3704, 3705, 4502];

    // Having an ID in the agent's array isn't proof of discovery, since locked entries have IDs too.
    public static bool IsRegionNameVisible(ushort placeNameId, bool hasDiscoveredHole, bool vanillaShowsName)
    {
        if (placeNameId == 0) return hasDiscoveredHole;
        if (Array.IndexOf(HiddenRegionPlaceNameIds, placeNameId) >= 0) return hasDiscoveredHole || vanillaShowsName;
        return true;
    }

    // The fishing spot's RowId is the discovery key (Order is only for sorting), and
    // rows outside the notebook's bitfield must not alias its bits.
    public static bool IsDiscovered(ReadOnlySpan<byte> flags, int bitCount, uint rowId)
    {
        if (bitCount <= 0 || rowId >= (uint)bitCount || rowId / 8 >= (uint)flags.Length)
            return false;
        return (flags[(int)(rowId / 8)] & (1 << (int)(rowId % 8))) != 0;
    }
}
