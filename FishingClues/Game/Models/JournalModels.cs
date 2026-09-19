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
    // Pixel position on the area's 2048x2048 map texture (1024,1024 is the centre).
    // Null when the zone has no usable map.
    Vector2? MapPixelPosition = null,
    // Game path of the area's map texture; null alongside MapPixelPosition.
    string? MapTexturePath = null,
    // The hole's world X/Z, used to find the nearest hole to the player.
    Vector2? WorldPosition = null,
    // The sheet's Radius, which sizes the hole's range circle on the map.
    int Radius = 0)
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

// A fish's heading with its catch-condition lines (bait, time, weather and so on).
public sealed record FishClueSection(string Heading, IReadOnlyList<string> Lines);
