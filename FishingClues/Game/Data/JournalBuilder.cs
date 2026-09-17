using Dalamud.Game.NativeWrapper;
using Dalamud.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FishParameterSheet = Lumina.Excel.Sheets.FishParameter;
using FishingSpotSheet = Lumina.Excel.Sheets.FishingSpot;
using ItemSheet = Lumina.Excel.Sheets.Item;
using MapSheet = Lumina.Excel.Sheets.Map;
using PlaceNameSheet = Lumina.Excel.Sheets.PlaceName;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.Game.Data;

// Builds the region/area/spot tree the journal and guide windows draw from,
// and tracks per-character reveal state (both ours and whatever the vanilla
// Fishing Log has already shown the player).
public sealed class JournalBuilder
{
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

    // Fired when the logged-in character changes, so other services can drop
    // their own per-character transient state (e.g. a pending log-replacement).
    public event Action? CharacterChanged;

    public JournalBuilder(FishDataService fishData, Configuration configuration)
    {
        this.fishData = fishData;
        this.configuration = configuration;
        fishData.DataRefreshed += () => journalCache = null;
    }

    public void InvalidateCache()
    {
        journalCache = null;
        lastJournalBuild = 0;
    }

    public unsafe IReadOnlyList<JournalRegion> GetJournal()
    {
        ResetJournalCharacter();
        if (journalCache is not null && Environment.TickCount64 - lastJournalBuild < 2000)
            return journalCache;

        FishDataFile data = fishData.Data;
        var spots = new List<JournalSpot>();
        PlayerState* player = PlayerState.Instance();
        byte* caught = player == null ? null : player->CaughtFishBitArray.Pointer;
        var fishSheet = Services.DataManager.GetExcelSheet<FishParameterSheet>();
        var itemSheet = Services.DataManager.GetExcelSheet<ItemSheet>();
        var fishByItemId = fishSheet
            .Where(fish => fish.Item.RowId != 0)
            .GroupBy(fish => fish.Item.RowId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (FishingSpotSheet spot in Services.DataManager.GetExcelSheet<FishingSpotSheet>())
        {
            if (spot.PlaceName.RowId == 0 || (spot.TerritoryType.RowId == 0 && spot.RowId != 10000 && spot.RowId < 10017))
                continue;

            var entries = new List<JournalFish>();
            foreach (var fishReference in spot.Item)
            {
                uint itemId = fishReference.RowId;
                if (itemId == 0 || !fishByItemId.TryGetValue(itemId, out FishParameterSheet fish))
                    continue;
                uint fishId = fish.RowId;
                FishInfo? info = data.Info.GetValueOrDefault(itemId);
                bool hasItem = itemSheet.TryGetRow(itemId, out ItemSheet item);
                string fishName = hasItem ? item.Name.ToString() : info?.Name ?? $"Fish #{itemId}";
                uint icon = hasItem ? item.Icon : info?.Icon ?? 0;
                bool caughtFlag = configuration.DebugRevealEverything || IsCaught(caught, fishId);
                entries.Add(new JournalFish(fishId, itemId, spot.GatheringLevel, caughtFlag, fishName, icon, info, spot.RowId,
                    configuration.RevealedFish.TryGetValue(journalCharacterId, out var revealed) && revealed.Contains(fishId)));
            }
            if (entries.Count == 0)
                continue;

            var territoryRow = spot.TerritoryType.ValueNullable;
            FishInfo? locationInfo = entries.Select(e => e.Info).FirstOrDefault(i => i is not null && !string.IsNullOrWhiteSpace(i.Region));
            string territory = territoryRow?.PlaceName.ValueNullable?.Name.ToString() ?? "Other";
            string region = spot.PlaceNameMain.ValueNullable?.Name.ToString() ?? string.Empty;
            string area = spot.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(area)) area = territory;
            if (string.IsNullOrWhiteSpace(region))
                region = territoryRow?.PlaceNameRegion.ValueNullable?.Name.ToString() ?? locationInfo?.Region ?? string.Empty;
            if (string.IsNullOrWhiteSpace(region) || region.Equals("Other", StringComparison.OrdinalIgnoreCase))
            {
                string? mappedRegion = null;
                if (!fishData.RegionByZone.TryGetValue(area, out mappedRegion))
                    fishData.RegionByZone.TryGetValue(territory, out mappedRegion);
                region = mappedRegion ?? RegionFromKnownArea(area);
            }
            string spotName = spot.PlaceName.ValueNullable?.Name.ToString() ?? $"Fishing Hole #{spot.RowId}";
            uint mapId = territoryRow?.Map.RowId ?? 0;
            uint order = spot.Order;
            bool isUnlocked = configuration.DebugRevealEverything || IsFishingHoleDiscovered(player, spot.RowId);
            ushort regionPlaceNameId = (ushort)(spot.PlaceNameMain.RowId != 0
                ? spot.PlaceNameMain.RowId : territoryRow?.PlaceNameRegion.RowId ?? 0);
            ushort spotPlaceNameId = (ushort)spot.PlaceName.RowId;
            MapSheet? mapRow = territoryRow?.Map.ValueNullable;
            (Vector2? mapPixel, string? mapTexturePath) = BuildMapInfo(mapRow, spot.X, spot.Z);
            spots.Add(new JournalSpot(spot.RowId, spotName, area, region, spot.TerritoryType.RowId, mapId,
                order, isUnlocked, regionPlaceNameId, spotPlaceNameId, entries, mapPixel, mapTexturePath));
        }

        journalCache = spots.GroupBy(s => s.Region)
            .OrderBy(g => RegionIndex(g.Key)).ThenBy(g => g.Min(s => s.Order)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(region => new JournalRegion(region.Key,
                region.Any(spot => FishingDiscovery.IsRegionNameVisible(spot.RegionPlaceNameId,
                    spot.IsUnlocked, vanillaRevealedRegions.Contains(spot.RegionPlaceNameId))),
                region.GroupBy(s => s.Area)
                .OrderBy(area => area.Min(s => s.Order)).ThenBy(area => area.Key, StringComparer.OrdinalIgnoreCase)
                .Select(area => new JournalArea(area.Key, area.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray()))
                .ToArray()))
            .ToArray();
        lastJournalBuild = Environment.TickCount64;
        return journalCache;
    }

    // Converts a fishing hole's raw world X/Z into a pixel position on its
    // area's 2048x2048 map texture (1024,1024 = center), using the same
    // Map SizeFactor/Offset the game itself uses to place markers, and
    // resolves the game path of that map's own texture.
    //
    // The world->human "map coordinate" step below is Dalamud's own vetted
    // MapUtil.WorldToMap; the human-coordinate->pixel step and the texture
    // path convention ("ui/map/{folder}/{variant}/{folder}{variant}_m.tex")
    // follow the widely-used FFXIV community formula/convention, not an
    // official Dalamud API - if a marker looks visibly off or the map image
    // doesn't load, that conversion is the first thing to check.
    private static (Vector2? Pixel, string? TexturePath) BuildMapInfo(MapSheet? map, short worldX, short worldZ)
    {
        if (map is not MapSheet mapRow || mapRow.RowId == 0)
            return (null, null);

        Vector2 humanCoordinate = MapUtil.WorldToMap(new Vector2(worldX, worldZ), mapRow);
        float scale = mapRow.SizeFactor / 100.0f;
        if (scale <= 0) return (null, null);
        var pixel = new Vector2(
            1024.0f + (humanCoordinate.X - 1.0f) * 50.0f * scale,
            1024.0f + (humanCoordinate.Y - 1.0f) * 50.0f * scale);

        string id = mapRow.Id.ToString();
        int slash = id.IndexOf('/');
        string? texturePath = slash > 0 && slash < id.Length - 1
            ? $"ui/map/{id[..slash]}/{id[(slash + 1)..]}/{id[..slash]}{id[(slash + 1)..]}_m.tex"
            : null;
        return (pixel, texturePath);
    }

    private unsafe void ResetJournalCharacter()
    {
        PlayerState* player = PlayerState.Instance();
        ulong character = player == null ? 0 : player->ContentId;
        if (journalCharacterId == character) return;
        journalCharacterId = character;
        vanillaRevealedRegions.Clear();
        journalCache = null;
        CharacterChanged?.Invoke();
    }

    public unsafe void ObserveVanillaRegionLabels()
    {
        ResetJournalCharacter();
        AtkUnitBasePtr ptr = Services.GameGui.GetAddonByName("FishingNote");
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (ptr.IsNull || !ptr.IsVisible || agent == null || agent->Mode != 0) return;
        ObserveRevealedFish(agent, (AtkUnitBase*)ptr.Address);
        var places = Services.DataManager.GetExcelSheet<PlaceNameSheet>();
        foreach (ushort id in new ushort[] { 3704, 3705, 4502 })
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
        nextFishRevealScan = Environment.TickCount64 + 500;
        var fishSheet = Services.DataManager.GetExcelSheet<FishParameterSheet>();
        if (!configuration.RevealedFish.TryGetValue(journalCharacterId, out var revealed))
            configuration.RevealedFish[journalCharacterId] = revealed = new HashSet<uint>();
        bool changed = false;
        int count = Math.Min(agent->FishSlotCount, (byte)agent->FishSlots.Length);
        for (int i = 0; i < count; i++)
        {
            uint id = agent->FishSlots[i].Id;
            if (id == 0 || revealed.Contains(id) || !fishSheet.TryGetRow(id, out var fish)) continue;
            var itemSheet = Services.DataManager.GetExcelSheet<ItemSheet>();
            string name = itemSheet.TryGetRow(fish.Item.RowId, out var item) ? item.Name.ToString() : fishData.Data.Items.GetValueOrDefault(fish.Item.RowId, $"Item #{fish.Item.RowId}");
            if (FishingNoteAddon.IsNameVisible(addon, name)) changed |= revealed.Add(id);
        }
        if (changed)
        {
            journalCache = null;
            Services.PluginInterface.SavePluginConfig(configuration);
        }
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
