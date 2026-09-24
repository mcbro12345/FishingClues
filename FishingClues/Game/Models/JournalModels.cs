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
    // pixel position on the area's 2048 map (1024,1024 is the centre), null if there's no map
    Vector2? MapPixelPosition = null,
    // map texture path, null along with MapPixelPosition
    string? MapTexturePath = null,
    // world X/Z, for finding the nearest hole
    Vector2? WorldPosition = null,
    // sheet Radius, sizes the range circle
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

public static class JournalExtensions
{
    public static IEnumerable<JournalSpot> AllSpots(this IEnumerable<JournalRegion> regions)
        => regions.SelectMany(r => r.Areas).SelectMany(a => a.Spots);
}

public sealed record FishClueSection(string Heading, IReadOnlyList<string> Lines);
