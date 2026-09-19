using System;
using System.Collections.Generic;

namespace FishingClues.Game.Models;

public sealed record GuideLocation(uint SpotId, string Region, string Area, string Hole, bool Spearfishing = false)
{
    public string Label => $"{Region}-{Area}-{Hole}";
}

public sealed record GuideDetails(string Name, IReadOnlyList<string> Info, IReadOnlyList<GuideLocation> Locations,
    Func<GuideLocation, IReadOnlyList<string>> GetDetails,
    IReadOnlyDictionary<string, uint> ItemLinks);
