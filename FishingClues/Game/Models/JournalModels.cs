using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FishingClues.Game.Models;

public sealed record JournalFish(
    uint FishParameterId,
    uint ItemId,
    byte Level,
    bool IsCaught,
    string Name,
    uint IconId,
    FishInfo? Info,
    uint SpotId = 0,
    bool IsRevealed = false)
{
    public bool IdentityVisible => IsCaught || IsRevealed;
}

public sealed record JournalSpot(
    uint Id,
    string Name,
    string Area,
    string Region,
    uint TerritoryId,
    uint MapId,
    uint Order,
    bool IsUnlocked,
    ushort RegionPlaceNameId,
    ushort SpotPlaceNameId,
    IReadOnlyList<JournalFish> Fish,
    // Pixel position on the area's 2048x2048 map texture (1024,1024 = map
    // center), already converted from the fishing hole's raw world X/Z via the
    // same Map SizeFactor/Offset the game itself uses. Null when the spot's
    // territory has no usable map (e.g. an instanced or non-field zone).
    Vector2? MapPixelPosition = null,
    // Game path of the area's map texture (e.g. "ui/map/s1d1/00/s1d100_m.tex"),
    // shared by every spot in the same territory. Null alongside MapPixelPosition.
    string? MapTexturePath = null)
{
    public int CaughtCount => Fish.Count(f => f.IsCaught);
    public int MissingCount => Fish.Count - CaughtCount;
}

public sealed record JournalArea(string Name, IReadOnlyList<JournalSpot> Spots)
{
    public bool IsUnlocked => Spots.Any(spot => spot.IsUnlocked);
    public uint Order => Spots.Count == 0 ? uint.MaxValue : Spots.Min(spot => spot.Order);
}

public sealed record JournalRegion(string Name, bool IsUnlocked, IReadOnlyList<JournalArea> Areas)
{
    public uint Order => Areas.Count == 0 ? uint.MaxValue : Areas.Min(area => area.Order);
}

// One named block of catch-condition text for a single fish (bait, time window, weather, and so on).
public sealed record FishClueSection(string Heading, IReadOnlyList<string> Lines);
