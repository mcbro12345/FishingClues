using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using KamiToolKit;
using FishParameterSheet = Lumina.Excel.Sheets.FishParameter;
using ItemSheet = Lumina.Excel.Sheets.Item;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Logic;
using FishingClues.Game.Models;
using FishingClues.UI.Windows;

namespace FishingClues.UI;

// Owns every ImGui/native window the plugin shows and the WindowSystem that
// hosts the Dalamud-side ones. Nothing outside this class touches a window
// instance directly.
public sealed class WindowManager
{
    private readonly Configuration configuration;
    private readonly FishDataService fishData;
    private readonly JournalBuilder journal;
    private readonly AvailabilityService availability;
    private readonly FishDetailsFormatter formatter;
    private readonly GuideDetailsService guideDetails;

    // Set by Plugin right after both this and the log-replacement controller
    // exist; the two depend on each other, so neither can be handed a fully
    // wired reference to the other in its own constructor.
    public Action OpenNormalFishingLog { get; set; } = () => { };

    private readonly WindowSystem windowSystem = new("FishingClues");
    private readonly DalamudClueWindow dalamudClues;
    private readonly DalamudSettingsWindow dalamudSettings;
    private readonly DiagnosticWindow diagnosticWindow;
    private readonly NativeJournalSessionState nativeJournalState = new();
    private readonly NativeJournalSessionState nativeGuideState = new();
    private readonly Task nativeUiInitialization;
    private bool nativeJournalWasOpen;
    private bool guideAutoClosedByLog;
    private NativeJournalWindow? nativeJournal;
    private NativeJournalWindow? nativeGuide;

    public bool IsNativeJournalOpen => nativeJournal?.IsOpen == true;

    public WindowManager(Configuration configuration, FishDataService fishData, JournalBuilder journal,
        AvailabilityService availability, FishDetailsFormatter formatter, GuideDetailsService guideDetails,
        Action logDiagnostics)
    {
        this.configuration = configuration;
        this.fishData = fishData;
        this.journal = journal;
        this.availability = availability;
        this.formatter = formatter;
        this.guideDetails = guideDetails;

        nativeUiInitialization = KamiToolKitLibrary.InitializeAsync(Services.PluginInterface, "Fishing Clues");
        dalamudClues = new DalamudClueWindow();
        dalamudSettings = new DalamudSettingsWindow(configuration, SaveConfiguration, ApplyLiveLayout, logDiagnostics,
            () => { _ = fishData.RefreshAsync(); }, () => fishData.IsRefreshing, () => fishData.StatusMessage,
            Services.KeyState, RefreshContents);
        diagnosticWindow = new DiagnosticWindow(logDiagnostics);
        windowSystem.AddWindow(diagnosticWindow);
        windowSystem.AddWindow(dalamudClues);
        windowSystem.AddWindow(dalamudSettings);
    }

    public void Draw() => windowSystem.Draw();

    public void ShowDiagnostics(string report) => diagnosticWindow.Show(report);

    public string DescribeMapState() => nativeJournal?.IsOpen == true
        ? nativeJournal.DescribeMapState()
        : "Area map: journal window is not currently open.";

    public string DescribeDetailsLayouts()
        => string.Join("\n", new[] { nativeJournal, nativeGuide }.Where(w => w?.IsOpen == true).Select(w => w!.DescribeDetailsLayout()));

    public void OpenSettings() => dalamudSettings.IsOpen = true;

    public void SaveConfiguration()
    {
        SaveLayout();
        dalamudClues.IsOpen = false;
    }

    private void SaveLayout() => Services.PluginInterface.SavePluginConfig(configuration);

    public void ApplyLiveLayout()
    {
        if (nativeJournal?.IsOpen != true)
            return;
        nativeJournal.ApplyLayout(
            Math.Clamp(configuration.NativeWindowWidth, 850.0f, 1600.0f),
            Math.Clamp(configuration.NativeWindowHeight, 450.0f, 1000.0f),
            configuration.NativeRegionWidth,
            configuration.NativeAreaWidth,
            configuration.NativeAreaDropdownLeftInset,
            configuration.NativeAreaDropdownRightInset,
            configuration.ShowOpenNormalLogButton);
    }

    public void OpenJournal()
    {
        journal.InvalidateCache();
        _ = OpenNativeJournalAsync();
    }

    // silenceOpenSound: the game's own Fishing Log that this window is replacing has
    // just played its open sound, so this one stays quiet rather than doubling it.
    public void ToggleNativeJournal(bool silenceOpenSound = false)
    {
        if (nativeJournal?.IsOpen == true)
            nativeJournal.Close();
        else
            _ = OpenNativeJournalAsync(silenceOpenSound);
    }

    public void CloseNativeJournal()
    {
        if (nativeJournal?.IsOpen == true)
            nativeJournal.Close();
    }

    // journal/guide windows build their fish list once at construction, so
    // a content change needs them torn down and reopened, not just redrawn
    public void RefreshContents()
    {
        journal.InvalidateCache();
        if (nativeJournal?.IsOpen == true) _ = OpenNativeJournalAsync();
        if (nativeGuide?.IsOpen == true) _ = OpenGuideAsync();
    }

    // called every frame: keeps the fish guide in step with the journal it was opened from
    public void SyncGuideWithJournal()
    {
        bool journalOpenNow = IsNativeJournalOpen;
        if (nativeJournalWasOpen && !journalOpenNow)
        {
            if (nativeGuide?.IsOpen == true)
            {
                guideAutoClosedByLog = true;
                nativeGuide.Close();
            }
        }
        else if (journalOpenNow && guideAutoClosedByLog)
        {
            guideAutoClosedByLog = false;
            _ = OpenGuideAsync();
        }
        nativeJournalWasOpen = journalOpenNow;
    }

    public async Task OpenNativeJournalAsync(bool silenceOpenSound = false)
    {
        try
        {
            // Start building the current zone's map now, alongside building the window.
            NativeJournalWindow.PrewarmMapCache(Services.ClientState.TerritoryType);
            await nativeUiInitialization;
            IReadOnlyList<JournalRegion> regions = journal.GetJournal();
            await Services.Framework.Run(() =>
            {
                nativeJournal?.Dispose();
                uint? pendingDiscovery = journal.ConsumePendingDiscoveredSpot();
                nativeJournal = new NativeJournalWindow(regions, nativeJournalState, configuration, new NativeJournalOptions
                {
                    RegionWidth = configuration.NativeRegionWidth,
                    AreaWidth = configuration.NativeAreaWidth,
                    AreaDropdownLeftInset = configuration.NativeAreaDropdownLeftInset,
                    AreaDropdownRightInset = configuration.NativeAreaDropdownRightInset,
                    ShowNormalLogButton = configuration.ShowOpenNormalLogButton,
                    OpenNormalLog = OpenNormalFishingLog,
                    BuildDetails = formatter.BuildFishSection,
                    ReportSetupError = ex => Services.Log.Error(ex, "Native journal button failed; using the text fallback."),
                    SaveLayout = SaveLayout,
                    BuildGuideDetails = guideDetails.BuildGuideDetails,
                    GetAvailability = availability.GetAvailability,
                    OpenGuide = () => _ = OpenGuideAsync(),
                    OpenSettings = OpenSettings,
                    PendingDiscoveredSpotId = pendingDiscovery,
                })
                {
                    InternalName = "FishingCluesJournalNative",
                    OpenWindowSoundEffectId = silenceOpenSound ? 0 : 23,
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
        catch (Exception ex) { Services.Log.Error(ex, "Could not open the native Fishing Clues journal."); }
    }

    public async Task OpenGuideAsync(string? query = null, uint selectItemId = 0)
    {
        guideAutoClosedByLog = false;
        try
        {
            await nativeUiInitialization;
            await Services.Framework.Run(() =>
            {
                var regions = journal.GetJournal();
                FishDataFile data = fishData.Data;
                var known = regions.SelectMany(r => r.Areas).SelectMany(a => a.Spots).SelectMany(s => s.Fish)
                    .GroupBy(f => f.ItemId).ToDictionary(g => g.Key, g => g.First());
                foreach (var row in Services.DataManager.GetExcelSheet<FishParameterSheet>())
                {
                    if (row.Item.RowId == 0 || known.ContainsKey(row.Item.RowId)) continue;
                    if (!Services.DataManager.GetExcelSheet<ItemSheet>().TryGetRow(row.Item.RowId, out var item)) continue;
                    known[row.Item.RowId] = new JournalFish(row.RowId, row.Item.RowId, 0, true,
                        item.Name.ToString(), item.Icon, data.Info.GetValueOrDefault(row.Item.RowId));
                }
                foreach (var location in data.Locations)
                {
                    if (known.ContainsKey(location.ItemId) || !Services.DataManager.GetExcelSheet<ItemSheet>().TryGetRow(location.ItemId, out var item)) continue;
                    known[location.ItemId] = new JournalFish(location.ItemId | 0x80000000, location.ItemId, 0, true,
                        item.Name.ToString(), item.Icon, data.Info.GetValueOrDefault(location.ItemId));
                }
                foreach (var pair in data.Info)
                {
                    if (known.ContainsKey(pair.Key)) continue;
                    known[pair.Key] = new JournalFish(pair.Key | 0x80000000, pair.Key, (byte)Math.Clamp(pair.Value.Level, 0, 255),
                        true, formatter.ItemName(pair.Key), pair.Value.Icon, pair.Value);
                }
                var fish = known.Values.Select(f => f with { IsRevealed = true, SpotId = 0 })
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                nativeGuide?.Dispose();
                nativeGuide = new NativeJournalWindow(regions, nativeGuideState, configuration, new NativeJournalOptions
                {
                    RegionWidth = 130,
                    AreaWidth = 240,
                    AreaDropdownLeftInset = 0,
                    AreaDropdownRightInset = 28,
                    ShowNormalLogButton = false,
                    OpenNormalLog = OpenNormalFishingLog,
                    BuildDetails = f => new FishClueSection(f.Name, Array.Empty<string>()),
                    ReportSetupError = ex => Services.Log.Error(ex, "Fish guide failed."),
                    SaveLayout = SaveLayout,
                    BuildGuideDetails = guideDetails.BuildGuideDetails,
                    GetAvailability = availability.GetAvailability,
                    GuideFish = fish,
                })
                {
                    InternalName = "FishingCluesGuideNative", Title = "Fish Guide", Subtitle = "Fishing Clues",
                    Size = new Vector2(560, 650), ContentPadding = new Vector2(14, 12), RememberClosePosition = true,
                };
                nativeGuide.Open();
                if (!string.IsNullOrWhiteSpace(query)) nativeGuide.SubmitSearch(query, selectItemId);
            });
        }
        catch (Exception ex) { Services.Log.Error(ex, "Could not open fish guide."); }
    }

    public void OpenClues(IReadOnlyList<HiddenFish> fish)
    {
        var sections = new List<FishClueSection>(fish.Count);
        for (int i = 0; i < fish.Count; i++)
        {
            HiddenFish entry = fish[i];
            string identity = entry.KnownName ?? (fish.Count == 1 ? "????" : $"???? #{i + 1}");
            sections.Add(new FishClueSection(identity,formatter.BuildRequirementLines(entry.ItemId)));
        }
        OpenSections(sections);
    }

    private void OpenSections(IReadOnlyList<FishClueSection> sections) => dalamudClues.Show(sections);

    public void Dispose()
    {
        windowSystem.RemoveAllWindows();
        if (nativeUiInitialization.IsCompletedSuccessfully)
        {
            nativeJournal?.Dispose();
            nativeGuide?.Dispose();
            NativeJournalWindow.ClearMapTextureCache();
            KamiToolKitLibrary.Dispose();
        }
    }
}
