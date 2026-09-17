using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using FishParameterSheet = Lumina.Excel.Sheets.FishParameter;
using FishingSpotSheet = Lumina.Excel.Sheets.FishingSpot;
using MainCommandSheet = Lumina.Excel.Sheets.MainCommand;
using ItemSheet = Lumina.Excel.Sheets.Item;
using PlaceNameSheet = Lumina.Excel.Sheets.PlaceName;

namespace FishingClues;

// Everything to do with turning game data into the JournalRegion/Area/Spot/Fish
// tree the rest of the plugin reads from - loading the cached fish conditions,
// pulling a fresh copy down, and walking the fishing-spot sheets to build (and
// cache) the journal itself.
public sealed partial class Plugin
{
    private static readonly string[] RegionOrder =
    [
        "La Noscea", "The Black Shroud", "Thanalan", "Coerthas", "Mor Dhona",
        "Abalathia's Spine", "Dravania", "Gyr Abania", "Othard", "Hingashi",
        "Norvrandt", "The Northern Empty", "Ilsabard",
        "The Sea of Stars", "The World Unsundered", "Yok Tural", "Xak Tural",
        "Unlost World", "The High Seas", "Other",
    ];

    private static FishDataFile LoadData()
    {
        foreach (string path in new[] { DataCachePath, Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, "FishConditions.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<FishDataFile>(File.ReadAllText(path), FishDataRefresh.JsonOptions)!;
                FishDataRefresh.Validate(loaded);
                return loaded;
            }
            catch (Exception ex) { Log.Warning(ex, "Could not load fishing data; trying bundled fallback."); }
        }
        return new FishDataFile();
    }

    private async Task RefreshFishDataAsync()
    {
        if (dataRefreshBusy || disposed) return;
        dataRefreshBusy = true;
        dataRefreshStatus = "Downloading the latest catch conditions and fish information...";
        nextDataCheck = DateTime.UtcNow.AddDays(1);
        try
        {
            var updated = await Task.Run(() => FishDataRefresh.Download(refreshCancellation.Token));
            if (disposed) return;
            if (updated.Fish.Count < data.Fish.Count * 0.9 || updated.Info.Count < data.Info.Count * 0.9)
                throw new InvalidDataException("The download unexpectedly removed too many fish records.");
            Directory.CreateDirectory(PluginInterface.GetPluginConfigDirectory());
            string temp = DataCachePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(updated), refreshCancellation.Token);
            refreshCancellation.Token.ThrowIfCancellationRequested();
            File.Move(temp, DataCachePath, true);
            await Framework.Run(() =>
            {
                if (disposed) return;
                data = updated;
                regionByZone.Clear();
                foreach (var info in updated.Info.Values)
                    if (!string.IsNullOrWhiteSpace(info.Zone) && !string.IsNullOrWhiteSpace(info.Region))
                        regionByZone.TryAdd(info.Zone, info.Region);
                journalCache = null;
                dataRefreshStatus = $"Updated {DateTime.Now:g}: {data.Fish.Count:N0} fish. Reopen the journal to refresh all panels.";
            });
        }
        catch (OperationCanceledException) { if (!disposed) dataRefreshStatus = "Refresh cancelled. Previous data kept."; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Fishing data refresh failed; previous cache retained.");
            dataRefreshStatus = "Refresh failed. Previous data kept; try again later.";
        }
        finally { dataRefreshBusy = false; }
    }

    private unsafe IReadOnlyList<JournalRegion> GetJournal()
    {
        ResetJournalCharacter();
        if (journalCache is not null && Environment.TickCount64 - lastJournalBuild < 2000)
            return journalCache;

        var spots = new List<JournalSpot>();
        PlayerState* player = PlayerState.Instance();
        byte* caught = player == null ? null : player->CaughtFishBitArray.Pointer;
        var fishSheet = DataManager.GetExcelSheet<FishParameterSheet>();
        var itemSheet = DataManager.GetExcelSheet<ItemSheet>();
        var placeNameSheet = DataManager.GetExcelSheet<PlaceNameSheet>();
        var fishByItemId = fishSheet
            .Where(fish => fish.Item.RowId != 0)
            .GroupBy(fish => fish.Item.RowId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (FishingSpotSheet spot in DataManager.GetExcelSheet<FishingSpotSheet>())
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
                if (!regionByZone.TryGetValue(area, out mappedRegion))
                    regionByZone.TryGetValue(territory, out mappedRegion);
                region = mappedRegion ?? RegionFromKnownArea(area);
            }
            string spotName = spot.PlaceName.ValueNullable?.Name.ToString() ?? $"Fishing Hole #{spot.RowId}";
            uint mapId = territoryRow?.Map.RowId ?? 0;
            uint order = spot.Order;
            bool isUnlocked = configuration.DebugRevealEverything || IsFishingHoleDiscovered(player, spot.RowId);
            ushort regionPlaceNameId = (ushort)(spot.PlaceNameMain.RowId != 0
                ? spot.PlaceNameMain.RowId : territoryRow?.PlaceNameRegion.RowId ?? 0);
            ushort spotPlaceNameId = (ushort)spot.PlaceName.RowId;
            spots.Add(new JournalSpot(spot.RowId, spotName, area, region, spot.TerritoryType.RowId, mapId,
                order, isUnlocked, regionPlaceNameId, spotPlaceNameId, entries));
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

    private unsafe void ResetJournalCharacter()
    {
        PlayerState* player = PlayerState.Instance();
        ulong character = player == null ? 0 : player->ContentId;
        if (journalCharacterId == character) return;
        normalLogReturnPending = false;
        normalLogWasVisible = false;
        normalLogCloseRequested = false;
        journalCharacterId = character;
        vanillaRevealedRegions.Clear();
        journalCache = null;
    }

    private unsafe void ObserveVanillaRegionLabels()
    {
        ResetJournalCharacter();
        AtkUnitBasePtr ptr = GameGui.GetAddonByName("FishingNote");
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (ptr.IsNull || !ptr.IsVisible || agent == null || agent->Mode != 0) return;
        ObserveRevealedFish(agent, (AtkUnitBase*)ptr.Address);
        var places = DataManager.GetExcelSheet<PlaceNameSheet>();
        foreach (ushort id in new ushort[] { 3704, 3705, 4502 })
        {
            if (vanillaRevealedRegions.Contains(id) || !places.TryGetRow(id, out var place)) continue;
            string name = place.Name.ToString();
            if (name.Length > 0 && IsNameVisibleInFishingLog((AtkUnitBase*)ptr.Address, name))
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
        var fishSheet = DataManager.GetExcelSheet<FishParameterSheet>();
        if (!configuration.RevealedFish.TryGetValue(journalCharacterId, out var revealed))
            configuration.RevealedFish[journalCharacterId] = revealed = new HashSet<uint>();
        bool changed = false;
        int count = Math.Min(agent->FishSlotCount, (byte)agent->FishSlots.Length);
        for (int i = 0; i < count; i++) {
            uint id = agent->FishSlots[i].Id;
            if (id == 0 || revealed.Contains(id) || !fishSheet.TryGetRow(id, out var fish)) continue;
            string name = ItemName(fish.Item.RowId);
            if (IsNameVisibleInFishingLog(addon, name)) changed |= revealed.Add(id);
        }
        if (changed) {
            journalCache = null;
            PluginInterface.SavePluginConfig(configuration);
        }
    }

    private static int RegionIndex(string region)
    {
        int index = Array.FindIndex(RegionOrder, item => item.Equals(region, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? RegionOrder.Length - 1 : index;
    }

    private static unsafe bool IsFishingHoleDiscovered(PlayerState* player, uint rowId)
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

    private unsafe static bool IsCaught(byte* caught, uint fishId)
        => caught != null && ((caught[fishId / 8] >> (byte)(fishId % 8)) & 1) != 0;
}
