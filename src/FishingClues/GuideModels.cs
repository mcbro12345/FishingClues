using System;
using System.Collections.Generic;
using System.Linq;

namespace FishingClues;

public sealed record GuideLocation(uint SpotId, string Region, string Area, string Hole, bool Spearfishing = false)
{
    public string Label => $"{Region}-{Area}-{Hole}";
}

public sealed record FishingPole(uint ItemId, string Name, int Ownership, int Level, int Gathering)
{
    public string Label => Name + (Ownership == 0 ? " (Equipped)" : Ownership == 1 ? " (Owned)" : "");
}

public sealed record GuideDetails(string Name, IReadOnlyList<string> Info, IReadOnlyList<GuideLocation> Locations,
    Func<GuideLocation, IReadOnlyList<FishingPole>> GetPoles,
    Func<GuideLocation, FishingPole?, IReadOnlyList<string>> GetDetails,
    IReadOnlyDictionary<string, uint> ItemLinks);

public static class PoleOrdering
{
    public static IReadOnlyList<FishingPole> Eligible(IEnumerable<FishingPole> poles, int level, uint requiredPole) =>
        Sort(poles.Where(p => level > 0 && p.Level <= level && (requiredPole == 0 || p.ItemId == requiredPole)));

    public static IReadOnlyList<FishingPole> Sort(IEnumerable<FishingPole> poles) => poles
        .GroupBy(p => p.ItemId)
        .Select(g => g.OrderBy(p => p.Ownership).ThenByDescending(p => p.Gathering).First())
        .OrderBy(p => p.Ownership).ThenByDescending(p => p.Level).ThenByDescending(p => p.Gathering)
        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}

