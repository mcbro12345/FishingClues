using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FishingClues;

public sealed class FishDataFile
{
    public int SchemaVersion { get; set; }
    public List<FishLocation> Locations { get; set; } = new();
    public Dictionary<uint, Dictionary<uint, SpotBaitInfo>> SpotBaits { get; set; } = new();
    public Dictionary<uint, FishCondition> Fish { get; set; } = new();
    public Dictionary<uint, string> Items { get; set; } = new();
    public Dictionary<uint, string> Weather { get; set; } = new();
    public Dictionary<uint, string> Folklore { get; set; } = new();
    public Dictionary<uint, FishInfo> Info { get; set; } = new();
}

public sealed class FishLocation
{
    public uint ItemId { get; set; }
    public uint SpotId { get; set; }
    public uint MapId { get; set; }
    public uint PlaceId { get; set; }
    public uint ZoneId { get; set; }
    public bool Spearfishing { get; set; }
}

public sealed class SpotBaitInfo
{
    public List<uint> Recommended { get; set; } = new();
    public List<uint> Observed { get; set; } = new();
    public int MinimumGathering { get; set; }
}

public sealed class FishInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public uint Icon { get; set; }
    public int Level { get; set; }
    public int Stars { get; set; }
    public string Waters { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public bool Collectable { get; set; }
    public int Rarity { get; set; }
}

public sealed class FishCondition
{
    public bool RequirementsKnown { get; set; }
    public bool IsMooch { get; set; }
    public List<uint> PreviousWeather { get; set; } = new();
    public List<uint> Weather { get; set; } = new();
    public double StartHour { get; set; }
    public double EndHour { get; set; } = 24;
    public List<uint> BaitPath { get; set; } = new();
    public List<uint> AlternativeBaits { get; set; } = new();
    public List<List<uint>> Predators { get; set; } = new();
    public int? IntuitionSeconds { get; set; }
    public uint? Folklore { get; set; }
    public bool? FishEyes { get; set; }
    public bool? Snagging { get; set; }
    public string? Lure { get; set; }
    public string? Hookset { get; set; }
    public string? Tug { get; set; }
    public string? Gig { get; set; }
    public string? SpearSpeed { get; set; }
    public object? DataMissing { get; set; }
}

public sealed record HiddenFish(uint FishParameterId, uint ItemId, byte Level, string? KnownName);
