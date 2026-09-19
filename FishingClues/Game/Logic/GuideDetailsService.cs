using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Models;

namespace FishingClues.Game.Logic;

// Builds the fish details panel: where a fish can be caught and what it needs there.
public sealed class GuideDetailsService
{
    private readonly FishDataService fishData;
    private readonly JournalBuilder journal;
    private readonly FishDetailsFormatter formatter;

    public GuideDetailsService(FishDataService fishData, JournalBuilder journal, FishDetailsFormatter formatter)
    {
        this.fishData = fishData;
        this.journal = journal;
        this.formatter = formatter;
    }

    public GuideDetails BuildGuideDetails(JournalFish fish)
    {
        FishDataFile data = fishData.Data;
        var info = new List<string>();
        var metadata = data.Info.GetValueOrDefault(fish.ItemId) ?? fish.Info;
        if ((fish.SpotId == 0 || fish.IdentityVisible) && !string.IsNullOrWhiteSpace(metadata?.Description)) info.Add(metadata.Description);
        var locations = new List<GuideLocation>();
        var spots = journal.GetJournal().SelectMany(r => r.Areas).SelectMany(a => a.Spots)
            .Where(s => s.Fish.Any(f => f.ItemId == fish.ItemId) && (fish.SpotId == 0 || fish.SpotId == s.Id)).ToArray();
        foreach (var spot in spots) locations.Add(new GuideLocation(spot.Id, spot.Region, spot.Area, spot.Name));
        if (fish.SpotId == 0)
        {
            foreach (var location in data.Locations.Where(l => l.ItemId == fish.ItemId &&
                (l.Spearfishing || !spots.Any(s => s.Id == l.SpotId))))
            {
                string Place(uint id) => Services.DataManager.GetExcelSheet<PlaceName>().TryGetRow(id, out var row) ? row.Name.ToString() : "Unknown";
                uint regionId = Services.DataManager.GetExcelSheet<TerritoryType>().Where(t => t.Map.RowId == location.MapId)
                    .Select(t => t.PlaceNameRegion.RowId).FirstOrDefault();
                locations.Add(new GuideLocation(location.SpotId, regionId == 0 ? metadata?.Region ?? "Unknown" : Place(regionId),
                    Place(location.PlaceId), Place(location.ZoneId), location.Spearfishing));
            }
        }
        var links = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (fish.SpotId == 0 || fish.IdentityVisible) links[fish.Name] = fish.ItemId;
        if (data.SpotBaits.TryGetValue(fish.ItemId, out var baitSpots))
            foreach (var id in baitSpots.Values.SelectMany(b => b.Recommended.Concat(b.Observed)).Distinct())
                links[formatter.ItemName(id)] = id;
        return new GuideDetails(fish.IdentityVisible ? fish.Name : "????", info,
            locations.DistinctBy(l => (l.SpotId, l.Spearfishing)).ToArray(),
            location => SelectedCatchDetails(fish.ItemId, location), links);
    }

    // these relic fish require a specific pole; ordinary fish don't
    private static uint RequiredPole(uint fish) => fish switch
    {
        38792 or 38793 => 38725,
        38798 or 38799 => 38736,
        39809 or 39810 => 38747,
        39815 or 39816 => 39742,
        41298 or 41302 => 39753,
        41300 or 41301 => 41190,
        _ => 0,
    };

    private int MinimumGathering(uint fish, uint hole) =>
        fishData.Data.SpotBaits.TryGetValue(fish, out var spots) && spots.TryGetValue(hole, out var entry) ? entry.MinimumGathering : 0;

    private IReadOnlyList<string> SelectedCatchDetails(uint fish, GuideLocation location)
    {
        var requirements = formatter.BuildRequirementLines(fish);
        var lines = new List<string> { requirements.FirstOrDefault(l => l.StartsWith("Hook:")) ?? "Hook: Unknown" };
        if (location.Spearfishing)
        {
            lines.Add("A spearfishing gig is required.");
            lines.Add("Bait: Not used.");
            return lines;
        }
        int minimum = MinimumGathering(fish, location.SpotId);
        if (minimum > 0) lines.Add($"Total Item Level required: {minimum} (all equipped gear).");
        if (RequiredPole(fish) != 0) lines.Add($"Required pole: {formatter.ItemName(RequiredPole(fish))}");
        if (fishData.Data.SpotBaits.TryGetValue(fish, out var spots) && spots.TryGetValue(location.SpotId, out var entry))
        {
            bool IsFish(uint id) => fishData.Data.Info.ContainsKey(id) || fishData.Data.Fish.ContainsKey(id);
            var ids = entry.Recommended.Concat(entry.Observed).Distinct().ToArray();
            var mooch = formatter.SortByItemLevel(ids.Where(IsFish)).ToArray();
            var bait = formatter.OrderBait(ids.Where(id => !IsFish(id))).Select(formatter.ItemName).ToArray();
            lines.Add("Bait: " + (bait.Length > 0 ? string.Join(", ", bait) : mooch.Length > 0 ? "None (mooch only)." : "Unknown."));
            lines.Add("Mooch: " + (mooch.Length > 0 ? string.Join(", ", mooch.Select(formatter.ItemName)) : "None."));
        }
        else { lines.Add("Bait: Unknown for this location."); lines.Add("Mooch: Unknown."); }
        lines.AddRange(requirements.Where(l => !l.StartsWith("Hook:") && l != "No special requirements." && l != "Fish Eyes: supported"));
        return lines;
    }
}
