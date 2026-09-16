using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace FishingClues;

public sealed partial class Plugin
{
    private List<Item>? poleCatalog;

    private GuideDetails BuildGuideDetails(JournalFish fish)
    {
        var info = new List<string>();
        var metadata = data.Info.GetValueOrDefault(fish.ItemId) ?? fish.Info;
        if ((fish.SpotId == 0 || fish.IdentityVisible) && !string.IsNullOrWhiteSpace(metadata?.Description)) info.Add(metadata.Description);
        var locations = new List<GuideLocation>();
        var spots = GetJournal().SelectMany(r => r.Areas).SelectMany(a => a.Spots)
            .Where(s => s.Fish.Any(f => f.ItemId == fish.ItemId) && (fish.SpotId == 0 || fish.SpotId == s.Id)).ToArray();
        foreach (var spot in spots) locations.Add(new GuideLocation(spot.Id, spot.Region, spot.Area, spot.Name));
        if (fish.SpotId == 0) {
            foreach (var location in data.Locations.Where(l => l.ItemId == fish.ItemId &&
                (l.Spearfishing || !spots.Any(s => s.Id == l.SpotId)))) {
                string Place(uint id) => DataManager.GetExcelSheet<PlaceName>().TryGetRow(id, out var row) ? row.Name.ToString() : "Unknown";
                uint regionId = DataManager.GetExcelSheet<TerritoryType>().Where(t => t.Map.RowId == location.MapId)
                    .Select(t => t.PlaceNameRegion.RowId).FirstOrDefault();
                locations.Add(new GuideLocation(location.SpotId, regionId == 0 ? metadata?.Region ?? "Unknown" : Place(regionId),
                    Place(location.PlaceId), Place(location.ZoneId), location.Spearfishing));
            }
        }
        var links = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (fish.SpotId == 0 || fish.IdentityVisible) links[fish.Name] = fish.ItemId;
        if (data.SpotBaits.TryGetValue(fish.ItemId, out var baitSpots))
            foreach (var id in baitSpots.Values.SelectMany(b => b.Recommended.Concat(b.Observed)).Distinct())
                links[ItemName(id)] = id;
        return new GuideDetails(fish.IdentityVisible ? fish.Name : "????", info,
            locations.DistinctBy(l => (l.SpotId, l.Spearfishing)).ToArray(),
            location => GetFishingPoles(fish.ItemId, location),
            (location, pole) => SelectedCatchDetails(fish.ItemId, location, pole), links);
    }

    // These relic fish explicitly require a particular tool. Ordinary fish are
    // selected by bait, location and total gathering, not a rod species list.
    private static uint RequiredPole(uint fish) => fish switch {
        38792 or 38793 => 38725,
        38798 or 38799 => 38736,
        39809 or 39810 => 38747,
        39815 or 39816 => 39742,
        41298 or 41302 => 39753,
        41300 or 41301 => 41190,
        _ => 0,
    };

    private int MinimumGathering(uint fish, uint hole) =>
        data.SpotBaits.TryGetValue(fish, out var spots) && spots.TryGetValue(hole, out var entry) ? entry.MinimumGathering : 0;

    private unsafe IReadOnlyList<FishingPole> GetFishingPoles(uint fish, GuideLocation location)
    {
        if (location.Spearfishing) return Array.Empty<FishingPole>();
        poleCatalog ??= DataManager.GetExcelSheet<Item>().Where(i => i.EquipSlotCategory.RowId != 0 &&
            i.ClassJobCategory.RowId != 0 && i.EquipSlotCategory.Value.MainHand == 1 && i.ClassJobCategory.Value.FSH &&
            !string.IsNullOrWhiteSpace(i.Name.ToString())).ToList();
        var owned = new Dictionary<uint, (int Rank, int Gathering)>();
        var inventory = InventoryManager.Instance();
        if (inventory != null) {
            foreach (var type in new[] { InventoryType.EquippedItems, InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.ArmoryMainHand }) {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded || container->Items == null) continue;
                for (int i = 0; i < container->Size; i++) {
                    var slot = container->Items + i;
                    uint id = slot->GetBaseItemId();
                    if (id == 0 || !DataManager.GetExcelSheet<Item>().TryGetRow(id, out var item)) continue;
                    int rank = type == InventoryType.EquippedItems ? 0 : 1;
                    int gathering = PoleGathering(item, (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
                    for (int j = 0; j < slot->Materia.Length; j++) {
                        if (slot->Materia[j] == 0 || !DataManager.GetExcelSheet<Materia>().TryGetRow(slot->Materia[j], out var materia)) continue;
                        int grade = slot->MateriaGrades[j];
                        if (materia.BaseParam.RowId == 72 && grade < materia.Value.Count) gathering += materia.Value[grade];
                    }
                    if (!owned.TryGetValue(id, out var previous) || rank < previous.Rank || (rank == previous.Rank && gathering > previous.Gathering))
                        owned[id] = (rank, gathering);
                }
            }
        }
        var player = PlayerState.Instance();
        int level = 0;
        if (player != null && DataManager.GetExcelSheet<ClassJob>().TryGetRow(18, out var fisher)) {
            int index = fisher.ExpArrayIndex;
            if (index >= 0 && index < player->ClassJobLevels.Length) level = player->ClassJobLevels[index];
        }
        uint required = RequiredPole(fish);
        var poles = new List<FishingPole>();
        foreach (var item in poleCatalog) {
            owned.TryGetValue(item.RowId, out var status);
            bool has = owned.ContainsKey(item.RowId);
            poles.Add(new FishingPole(item.RowId, item.Name.ToString(), has ? status.Rank : 2,
                item.LevelEquip, has ? status.Gathering : PoleGathering(item, false)));
        }
        return PoleOrdering.Eligible(poles, level, required);
    }

    private static int PoleGathering(Item item, bool hq)
    {
        int value = 0;
        for (int i = 0; i < item.BaseParam.Count; i++) if (item.BaseParam[i].RowId == 72) value += item.BaseParamValue[i];
        if (hq) for (int i = 0; i < item.BaseParamSpecial.Count; i++)
            if (item.BaseParamSpecial[i].RowId == 72) value += item.BaseParamValueSpecial[i];
        return value;
    }

    private IReadOnlyList<string> SelectedCatchDetails(uint fish, GuideLocation location, FishingPole? pole)
    {
        var requirements = BuildRequirementLines(fish);
        var lines = new List<string> { requirements.FirstOrDefault(l => l.StartsWith("Hook:")) ?? "Hook: Unknown" };
        if (location.Spearfishing) {
            lines.Add("A spearfishing gig is required.");
            lines.Add("Bait: Not used.");
            return lines;
        }
        if (pole is not null) lines.Add($"Pole Gathering: {pole.Gathering}");
        else lines.Add("No eligible fishing pole at your Fisher level.");
        int minimum = MinimumGathering(fish, location.SpotId);
        if (minimum > 0) lines.Add($"Total gathering required: {minimum} (all equipped gear).");
        if (RequiredPole(fish) != 0) lines.Add($"Required pole: {ItemName(RequiredPole(fish))}");
        if (data.SpotBaits.TryGetValue(fish, out var spots) && spots.TryGetValue(location.SpotId, out var entry)) {
            bool IsFish(uint id) => data.Info.ContainsKey(id) || data.Fish.ContainsKey(id);
            var ids = entry.Recommended.Concat(entry.Observed).Distinct().ToArray();
            var mooch = ids.Where(IsFish).ToArray();
            var bait = ids.Where(id => !IsFish(id)).OrderBy(id => DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(id, out var baitItem) ? baitItem.LevelEquip : uint.MaxValue).ThenBy(ItemName).Select(ItemName).ToArray();
            lines.Add("Bait: " + (bait.Length > 0 ? string.Join(", ", bait) : mooch.Length > 0 ? "None (mooch only)." : "Unknown."));
            lines.Add("Mooch: " + (mooch.Length > 0 ? string.Join(", ", mooch.Select(ItemName)) : "None."));
        } else { lines.Add("Bait: Unknown for this location."); lines.Add("Mooch: Unknown."); }
        lines.AddRange(requirements.Where(l => !l.StartsWith("Hook:") && l != "No special requirements." && l != "Fish Eyes: supported"));
        return lines;
    }
}
