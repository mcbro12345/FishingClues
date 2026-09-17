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

// The entry points for every window the plugin can put on screen: the
// journal, the settings panel, the fish guide, and the clue popups for a
// single fish or a batch of hidden ones.
public sealed partial class Plugin
{
    private void OpenJournal()
    {
        normalLogReturnPending = false;
        journalCache = null;
        lastJournalBuild = 0;
        if (configuration.UiMode == JournalUiMode.Dalamud)
            dalamudJournal.IsOpen = true;
        else
            _ = OpenNativeJournalAsync();
    }

    private void ToggleReplacementJournal()
    {
        if (configuration.UiMode == JournalUiMode.Dalamud)
        {
            dalamudJournal.IsOpen = !dalamudJournal.IsOpen;
            return;
        }
        if (nativeJournal?.IsOpen == true)
            nativeJournal.Close();
        else
            _ = OpenNativeJournalAsync();
    }

    private void OpenSettings() => dalamudSettings.IsOpen = true;

    // The Dalamud journal re-reads GetJournal() every draw, so dropping the
    // cache is enough for it. The native journal and guide build their fish
    // list once from whatever regions they were constructed with, so a
    // content change (not just a layout one) needs either of them, if open,
    // torn down and reopened to actually show it.
    private void RefreshJournalContents()
    {
        journalCache = null;
        lastJournalBuild = 0;
        if (nativeJournal?.IsOpen == true) _ = OpenNativeJournalAsync();
        if (nativeGuide?.IsOpen == true) _ = OpenGuideAsync();
    }

    private async Task OpenNativeJournalAsync()
    {
        try
        {
            await nativeUiInitialization;
            IReadOnlyList<JournalRegion> regions = GetJournal();
            await Framework.Run(() =>
            {
                nativeJournal?.Dispose();
                nativeJournal = new NativeJournalWindow(regions, nativeJournalState,
                    configuration.NativeRegionWidth, configuration.NativeAreaWidth,
                    configuration.NativeAreaDropdownWidth, configuration.ShowOpenNormalLogButton,
                    OpenFish, OpenNormalFishingLog, configuration, BuildFishSection,
                    ex => Log.Error(ex, "Native journal button failed; using the text fallback."),
                    () => PluginInterface.SavePluginConfig(configuration), AddonEvents,
                    () => _ = OpenGuideAsync(), guideDetails: BuildGuideDetails, getAvailability: GetFishAvailability)
                {
                    InternalName = "FishingCluesJournalNative",
                    Title = "Fishing Log",
                    Subtitle = "Fishing Clues",
                    Size = new Vector2(
                        Math.Clamp(configuration.NativeWindowWidth, 850.0f, 1600.0f),
                        Math.Clamp(configuration.NativeWindowHeight, 450.0f, 1000.0f)),
                    ContentPadding = new Vector2(14.0f, 12.0f),
                    RememberClosePosition = true,
                };
                nativeJournal.Open();
            });
        }
        catch (Exception ex) { Log.Error(ex, "Could not open the native Fishing Clues journal."); }
    }

    private async Task OpenGuideAsync(string? query = null, uint selectItemId = 0)
    {
        guideAutoClosedByLog = false;
        try {
            await nativeUiInitialization;
            await Framework.Run(() => {
                if (disposed) return;
                var regions = GetJournal();
                var known = regions.SelectMany(r => r.Areas).SelectMany(a => a.Spots).SelectMany(s => s.Fish)
                    .GroupBy(f => f.ItemId).ToDictionary(g => g.Key, g => g.First());
                // Include game fish even if no unlocked or known journal spot references them.
                foreach (var row in DataManager.GetExcelSheet<FishParameterSheet>()) {
                    if (row.Item.RowId == 0 || known.ContainsKey(row.Item.RowId)) continue;
                    if (!DataManager.GetExcelSheet<ItemSheet>().TryGetRow(row.Item.RowId, out var item)) continue;
                    known[row.Item.RowId] = new JournalFish(row.RowId, row.Item.RowId, 0, true,
                        item.Name.ToString(), item.Icon, data.Info.GetValueOrDefault(row.Item.RowId));
                }
                foreach (var location in data.Locations) {
                    if (known.ContainsKey(location.ItemId) || !DataManager.GetExcelSheet<ItemSheet>().TryGetRow(location.ItemId, out var item)) continue;
                    known[location.ItemId] = new JournalFish(location.ItemId | 0x80000000, location.ItemId, 0, true,
                        item.Name.ToString(), item.Icon, data.Info.GetValueOrDefault(location.ItemId));
                }
                foreach (var pair in data.Info) {
                    if (known.ContainsKey(pair.Key)) continue;
                    known[pair.Key] = new JournalFish(pair.Key | 0x80000000, pair.Key, (byte)Math.Clamp(pair.Value.Level, 0, 255),
                        true, ItemName(pair.Key), pair.Value.Icon, pair.Value);
                }
                var fish = known.Values.Select(f => f with { IsRevealed = true, SpotId = 0 })
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                nativeGuide?.Dispose();
                nativeGuide = new NativeJournalWindow(regions, nativeGuideState,
                    130, 240, 200, false, OpenFish, OpenNormalFishingLog, configuration, f => new FishClueSection(f.Name, Array.Empty<string>()),
                    ex => Log.Error(ex, "Fish guide failed."),
                    () => PluginInterface.SavePluginConfig(configuration), AddonEvents, guideFish: fish, guideDetails: BuildGuideDetails,
                    getAvailability: GetFishAvailability) {
                    InternalName = "FishingCluesGuideNative", Title = "Fish Guide", Subtitle = "Fishing Clues",
                    Size = new Vector2(560, 650), ContentPadding = new Vector2(14, 12), RememberClosePosition = true,
                };
                nativeGuide.Open();
                if (!string.IsNullOrWhiteSpace(query)) nativeGuide.SubmitSearch(query, selectItemId);
            });
        } catch (Exception ex) { Log.Error(ex, "Could not open fish guide."); }
    }

    private void OpenFish(JournalFish fish)
        => OpenSections([BuildFishSection(fish)]);

    private void OpenClues(IReadOnlyList<HiddenFish> fish)
    {
        var sections = new List<FishClueSection>(fish.Count);
        for (int i = 0; i < fish.Count; i++)
        {
            HiddenFish entry = fish[i];
            string identity = entry.KnownName ?? (fish.Count == 1 ? "????" : $"???? #{i + 1}");
            string heading = entry.Level > 0 ? $"{identity}   Lv. {entry.Level}" : identity;
            sections.Add(new FishClueSection(heading, BuildRequirementLines(entry.ItemId)));
        }
        OpenSections(sections);
    }

    private void OpenSections(IReadOnlyList<FishClueSection> sections)
    {
        if (!configuration.UseNativeFishDetails)
        {
            dalamudClues.Show(sections);
            return;
        }
        _ = OpenNativeCluesAsync(sections);
    }

    private async Task OpenNativeCluesAsync(IReadOnlyList<FishClueSection> sections)
    {
        try
        {
            await nativeUiInitialization;
            await Framework.Run(() =>
            {
                nativeClues?.Dispose();
                nativeClues = new FishClueWindow(sections)
                {
                    InternalName = "FishingCluesDetailsNative",
                    Title = "Fish Details",
                    Subtitle = "Fishing Clues",
                    Size = new Vector2(520.0f, 540.0f),
                    ContentPadding = new Vector2(14.0f, 12.0f),
                    RememberClosePosition = true,
                };
                nativeClues.Open();
            });
        }
        catch (Exception ex) { Log.Error(ex, "Could not open the native fish-details window."); }
    }
}
