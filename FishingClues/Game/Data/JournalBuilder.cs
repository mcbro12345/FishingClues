using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.NativeWrapper;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FishParameterSheet = Lumina.Excel.Sheets.FishParameter;
using FishingSpotSheet = Lumina.Excel.Sheets.FishingSpot;
using ItemSheet = Lumina.Excel.Sheets.Item;
using PlaceNameSheet = Lumina.Excel.Sheets.PlaceName;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.Game.Data;

// Builds the region / area / fishing hole tree the journal and guide draw from,
// and tracks which fish and regions the player has seen revealed.
public sealed class JournalBuilder
{
    private const long JournalCacheMs = 2000;
    private const long FishRevealScanIntervalMs = 500;
    private static readonly string[] RegionOrder =
    [
        "La Noscea", "The Black Shroud", "Thanalan", "Coerthas", "Mor Dhona",
        "Abalathia's Spine", "Dravania", "Gyr Abania", "Othard", "Hingashi",
        "Norvrandt", "The Northern Empty", "Ilsabard",
        "The Sea of Stars", "The World Unsundered", "Yok Tural", "Xak Tural",
        "Unlost World", "The High Seas", "Other",
    ];

    private readonly FishDataService fishData;
    private readonly Configuration configuration;
    private readonly HashSet<ushort> vanillaRevealedRegions = new();
    private IReadOnlyList<JournalRegion>? journalCache;
    private long lastJournalBuild;
    private ulong journalCharacterId;
    private long nextFishRevealScan;
    // The holes that were unlocked at the last build, so one that flips to
    // unlocked can be reported once as "just discovered". Null until the first
    // build, which only seeds it.
    private HashSet<uint>? knownUnlockedSpots;

    // A hole seen becoming unlocked since the journal window was last opened.
    public uint? PendingDiscoveredSpotId { get; private set; }

    // Raised when the logged-in character changes, so other services can drop
    // their per-character state.
    public event Action? CharacterChanged;

    public JournalBuilder(FishDataService fishData, Configuration configuration)
    {
        this.fishData = fishData;
        this.configuration = configuration;
        fishData.DataRefreshed += () => journalCache = null;
    }

    public uint? ConsumePendingDiscoveredSpot()
    {
        uint? id = PendingDiscoveredSpotId;
        PendingDiscoveredSpotId = null;
        return id;
    }

    public void InvalidateCache()
    {
        journalCache = null;
        lastJournalBuild = 0;
    }

    public unsafe IReadOnlyList<JournalRegion> GetJournal()
    {
        ResetJournalCharacter();
        if (journalCache is not null && Environment.TickCount64 - lastJournalBuild < JournalCacheMs)
            return journalCache;

        PlayerState* player = PlayerState.Instance();
        byte* caught = player == null ? null : player->CaughtFishBitArray.Pointer;
        var fishByItemId = Services.DataManager.GetExcelSheet<FishParameterSheet>()
            .Where(fish => fish.Item.RowId != 0)
            .GroupBy(fish => fish.Item.RowId)
            .ToDictionary(group => group.Key, group => group.First());

        var spots = new List<JournalSpot>();
        foreach (FishingSpotSheet spot in Services.DataManager.GetExcelSheet<FishingSpotSheet>())
        {
            if (spot.PlaceName.RowId == 0 || (spot.TerritoryType.RowId == 0 && spot.RowId != 10000 && spot.RowId < 10017))
                continue;
            var entries = BuildFishEntries(spot, fishByItemId, caught);
            if (entries.Count == 0) continue;
            spots.Add(BuildSpot(spot, entries, player));
        }

        if (!configuration.DebugRevealEverything)
            UpdateDiscoveryTracking(spots);

        journalCache = GroupIntoRegions(spots);
        lastJournalBuild = Environment.TickCount64;
        return journalCache;
    }

    private unsafe List<JournalFish> BuildFishEntries(FishingSpotSheet spot, Dictionary<uint, FishParameterSheet> fishByItemId, byte* caught)
    {
        var itemSheet = Services.DataManager.GetExcelSheet<ItemSheet>();
        configuration.RevealedFish.TryGetValue(journalCharacterId, out var revealed);
        var entries = new List<JournalFish>();
        foreach (var fishReference in spot.Item)
        {
            uint itemId = fishReference.RowId;
            if (itemId == 0 || !fishByItemId.TryGetValue(itemId, out FishParameterSheet fish))
                continue;
            FishInfo? info = fishData.Data.Info.GetValueOrDefault(itemId);
            bool hasItem = itemSheet.TryGetRow(itemId, out ItemSheet item);
            string name = hasItem ? item.Name.ToString() : info?.Name ?? $"Fish #{itemId}";
            uint icon = hasItem ? item.Icon : info?.Icon ?? 0;
            bool isCaught = configuration.DebugRevealEverything || IsCaught(caught, fish.RowId);
            entries.Add(new JournalFish(fish.RowId, itemId, spot.GatheringLevel, isCaught, name, icon, info, spot.RowId,
                revealed is not null && revealed.Contains(fish.RowId)));
        }
        return entries;
    }

    private unsafe JournalSpot BuildSpot(FishingSpotSheet spot, List<JournalFish> entries, PlayerState* player)
    {
        var territoryRow = spot.TerritoryType.ValueNullable;
        (string region, string area) = ResolveRegionAndArea(spot, territoryRow, entries);
        string name = spot.PlaceName.ValueNullable?.Name.ToString() ?? $"Fishing Hole #{spot.RowId}";
        bool isUnlocked = configuration.DebugRevealEverything || IsFishingHoleDiscovered(player, spot.RowId);
        ushort regionPlaceNameId = (ushort)(spot.PlaceNameMain.RowId != 0
            ? spot.PlaceNameMain.RowId : territoryRow?.PlaceNameRegion.RowId ?? 0);
        // FishingSpot.X and Z are already pixel positions on the 2048x2048 map
        // texture (1024,1024 is the centre), so no world-to-map conversion applies.
        var map = territoryRow?.Map.ValueNullable;
        bool hasMap = map is { RowId: not 0 };
        Vector2? mapPixel = hasMap ? new Vector2(spot.X, spot.Z) : null;
        string? mapTexturePath = hasMap ? MapTextures.PathFor(map!.Value) : null;
        return new JournalSpot(spot.RowId, name, area, region, spot.TerritoryType.RowId, territoryRow?.Map.RowId ?? 0,
            spot.Order, isUnlocked, regionPlaceNameId, (ushort)spot.PlaceName.RowId, entries, mapPixel, mapTexturePath,
            new Vector2(spot.X, spot.Z), spot.Radius);
    }

    // The sheet's region and area names are often blank, so fall back through the
    // territory, the fish data's own zone info, and finally a name match.
    private (string Region, string Area) ResolveRegionAndArea(FishingSpotSheet spot, TerritoryTypeSheet? territoryRow, List<JournalFish> entries)
    {
        string territory = territoryRow?.PlaceName.ValueNullable?.Name.ToString() ?? "Other";
        string region = spot.PlaceNameMain.ValueNullable?.Name.ToString() ?? string.Empty;
        string area = spot.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(area)) area = territory;
        if (string.IsNullOrWhiteSpace(region))
        {
            FishInfo? locationInfo = entries.Select(e => e.Info).FirstOrDefault(i => i is not null && !string.IsNullOrWhiteSpace(i.Region));
            region = territoryRow?.PlaceNameRegion.ValueNullable?.Name.ToString() ?? locationInfo?.Region ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(region) || region.Equals("Other", StringComparison.OrdinalIgnoreCase))
        {
            if (!fishData.RegionByZone.TryGetValue(area, out string? mappedRegion))
                fishData.RegionByZone.TryGetValue(territory, out mappedRegion);
            region = mappedRegion ?? RegionFromKnownArea(area);
        }
        return (region, area);
    }

    private IReadOnlyList<JournalRegion> GroupIntoRegions(List<JournalSpot> spots)
        => spots.GroupBy(s => s.Region)
            .OrderBy(g => RegionIndex(g.Key)).ThenBy(g => g.Min(s => s.Order)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(region => new JournalRegion(region.Key,
                region.Any(spot => FishingDiscovery.IsRegionNameVisible(spot.RegionPlaceNameId,
                    spot.IsUnlocked, vanillaRevealedRegions.Contains(spot.RegionPlaceNameId))),
                region.GroupBy(s => s.Area)
                    .OrderBy(area => area.Min(s => s.Order)).ThenBy(area => area.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(area => new JournalArea(area.Key, area.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray()))
                    .ToArray()))
            .ToArray();

    private unsafe void ResetJournalCharacter()
    {
        PlayerState* player = PlayerState.Instance();
        ulong character = player == null ? 0 : player->ContentId;
        if (journalCharacterId == character) return;
        journalCharacterId = character;
        vanillaRevealedRegions.Clear();
        journalCache = null;
        knownUnlockedSpots = null;
        PendingDiscoveredSpotId = null;
        CharacterChanged?.Invoke();
    }

    // Compares this build's unlocked holes with the last build's. The first build
    // only seeds the baseline; after that a hole becoming unlocked is the pending
    // discovery (the latest one, if several changed at once).
    private void UpdateDiscoveryTracking(List<JournalSpot> spots)
    {
        var currentUnlocked = new HashSet<uint>(spots.Where(s => s.IsUnlocked).Select(s => s.Id));
        if (knownUnlockedSpots is not null)
        {
            foreach (uint id in currentUnlocked)
                if (!knownUnlockedSpots.Contains(id))
                    PendingDiscoveredSpotId = id;
        }
        knownUnlockedSpots = currentUnlocked;
    }

    // Watches the vanilla Fishing Log while it is open, to learn which region
    // names and fish it has revealed to the player.
    public unsafe void ObserveVanillaRegionLabels()
    {
        ResetJournalCharacter();
        AtkUnitBasePtr ptr = Services.GameGui.GetAddonByName("FishingNote");
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (ptr.IsNull || !ptr.IsVisible || agent == null || agent->Mode != 0) return;
        ObserveRevealedFish(agent, (AtkUnitBase*)ptr.Address);
        var places = Services.DataManager.GetExcelSheet<PlaceNameSheet>();
        foreach (ushort id in FishingDiscovery.HiddenRegionPlaceNameIds)
        {
            if (vanillaRevealedRegions.Contains(id) || !places.TryGetRow(id, out var place)) continue;
            string name = place.Name.ToString();
            if (name.Length > 0 && FishingNoteAddon.IsNameVisible((AtkUnitBase*)ptr.Address, name))
            {
                vanillaRevealedRegions.Add(id);
                journalCache = null;
            }
        }
    }

    private unsafe void ObserveRevealedFish(AgentFishingNote* agent, AtkUnitBase* addon)
    {
        if (journalCharacterId == 0 || Environment.TickCount64 < nextFishRevealScan) return;
        nextFishRevealScan = Environment.TickCount64 + FishRevealScanIntervalMs;
        var fishSheet = Services.DataManager.GetExcelSheet<FishParameterSheet>();
        var itemSheet = Services.DataManager.GetExcelSheet<ItemSheet>();
        if (!configuration.RevealedFish.TryGetValue(journalCharacterId, out var revealed))
            configuration.RevealedFish[journalCharacterId] = revealed = new HashSet<uint>();
        bool changed = false;
        int count = Math.Min(agent->FishSlotCount, (byte)agent->FishSlots.Length);
        for (int i = 0; i < count; i++)
        {
            uint id = agent->FishSlots[i].Id;
            if (id == 0 || revealed.Contains(id) || !fishSheet.TryGetRow(id, out var fish)) continue;
            string name = itemSheet.TryGetRow(fish.Item.RowId, out var item)
                ? item.Name.ToString()
                : fishData.Data.Items.GetValueOrDefault(fish.Item.RowId, $"Item #{fish.Item.RowId}");
            if (FishingNoteAddon.IsNameVisible(addon, name)) changed |= revealed.Add(id);
        }
        if (changed)
        {
            journalCache = null;
            Services.PluginInterface.SavePluginConfig(configuration);
        }
    }

    // Where the player is now, for opening the journal at their location: the
    // region and area of the current zone, and the nearest unlocked hole in it
    // (null if the zone has none). All null if the zone isn't in the journal.
    public static (JournalRegion? Region, JournalArea? Area, JournalSpot? Spot) LocateCurrentLocation(IReadOnlyList<JournalRegion> regions)
    {
        uint territory = Services.ClientState.TerritoryType;
        if (territory == 0) return (null, null, null);
        Vector3? playerPosition = Services.ObjectTable.LocalPlayer?.Position;

        JournalRegion? matchedRegion = null;
        JournalArea? matchedArea = null;
        JournalSpot? nearestUnlocked = null;
        float nearestDistance = float.MaxValue;
        foreach (JournalRegion region in regions)
        {
            foreach (JournalArea area in region.Areas)
            {
                foreach (JournalSpot spot in area.Spots)
                {
                    if (spot.TerritoryId != territory) continue;
                    matchedRegion ??= region;
                    matchedArea ??= area;
                    if (!spot.IsUnlocked || spot.WorldPosition is not Vector2 worldPos || playerPosition is not Vector3 pos)
                        continue;
                    float distance = Vector2.DistanceSquared(worldPos, new Vector2(pos.X, pos.Z));
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestUnlocked = spot;
                    }
                }
            }
        }
        return (matchedRegion, matchedArea, nearestUnlocked);
    }

    private static int RegionIndex(string region)
    {
        int index = Array.FindIndex(RegionOrder, item => item.Equals(region, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? RegionOrder.Length - 1 : index;
    }

    internal static unsafe bool IsFishingHoleDiscovered(PlayerState* player, uint rowId)
    {
        if (player == null) return false;
        var flags = player->UnlockedFishingSpotsBitArray;
        return FishingDiscovery.IsDiscovered(new ReadOnlySpan<byte>(flags.Pointer, flags.ByteLength), flags.BitCount, rowId);
    }

    private static string RegionFromKnownArea(string area)
    {
        if (area.Contains("La Noscea", StringComparison.OrdinalIgnoreCase) || area.Contains("Limsa Lominsa", StringComparison.OrdinalIgnoreCase)) return "La Noscea";
        if (area.Contains("Shroud", StringComparison.OrdinalIgnoreCase) || area.Contains("Gridania", StringComparison.OrdinalIgnoreCase)) return "The Black Shroud";
        if (area.Contains("Thanalan", StringComparison.OrdinalIgnoreCase) || area.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase)) return "Thanalan";
        if (area.Contains("Coerthas", StringComparison.OrdinalIgnoreCase)) return "Coerthas";
        if (area.Contains("Mor Dhona", StringComparison.OrdinalIgnoreCase)) return "Mor Dhona";
        if (area is "Azys Lla" or "The Sea of Clouds") return "Abalathia's Spine";
        if (area.Contains("Dravanian", StringComparison.OrdinalIgnoreCase) || area is "The Churning Mists") return "Dravania";
        return "Other";
    }

    private static unsafe bool IsCaught(byte* caught, uint fishId)
        => caught != null && ((caught[fishId / 8] >> (byte)(fishId % 8)) & 1) != 0;
}
