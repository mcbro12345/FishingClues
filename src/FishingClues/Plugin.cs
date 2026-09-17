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

public sealed partial class Plugin : IDalamudPlugin, IDisposable
{
    private const string CommandName = "/fishingclues";
    private static readonly string[] RegionOrder =
    [
        "La Noscea", "The Black Shroud", "Thanalan", "Coerthas", "Mor Dhona",
        "Abalathia's Spine", "Dravania", "Gyr Abania", "Othard", "Hingashi",
        "Norvrandt", "The Northern Empty", "Ilsabard",
        "The Sea of Stars", "The World Unsundered", "Yok Tural", "Xak Tural",
        "Unlost World", "The High Seas", "Other",
    ];

    private FishDataFile data;
    private readonly System.Threading.CancellationTokenSource refreshCancellation = new();
    private bool dataRefreshBusy;
    private bool disposed;
    private DateTime nextDataCheck = DateTime.UtcNow;
    private string dataRefreshStatus = "Using bundled fishing data.";
    private static string DataCachePath => Path.Combine(PluginInterface.GetPluginConfigDirectory(), "FishConditions.json");
    private readonly Configuration configuration;
    private readonly Task nativeUiInitialization;
    private readonly Dictionary<string, string> regionByZone;
    private readonly WindowSystem windowSystem = new("FishingClues");
    private readonly DalamudJournalWindow dalamudJournal;
    private readonly DalamudClueWindow dalamudClues;
    private readonly DalamudSettingsWindow dalamudSettings;
    private readonly DiagnosticWindow diagnosticWindow;
    private readonly NativeJournalSessionState nativeJournalState = new();
    private readonly NativeJournalSessionState nativeGuideState = new();
    private bool nativeJournalWasOpen;
    private bool guideAutoClosedByLog;
    private bool journalKeybindWasDown;
    private NativeJournalWindow? nativeJournal;
    private NativeJournalWindow? nativeGuide;
    private FishClueWindow? nativeClues;
    private IReadOnlyList<JournalRegion>? journalCache;
    private long lastJournalBuild;
    private ulong journalCharacterId;
    private readonly HashSet<ushort> vanillaRevealedRegions = new();
    private long nextFishRevealScan;
    private long lastClickHandled;
    private long lastReplacement;
    private long pendingNormalLogUntil;
    private long pendingNormalLogSelectionAppliedAt;
    private JournalSpot? pendingNormalLogSpot;
    private bool allowExplicitVanillaLog;
    private bool normalLogWasVisible;
    private bool normalLogCloseRequested;
    private bool normalLogReturnPending;
    private long normalLogReturnAfter;
    private string normalLogReturnStatus = "No normal-log close observed.";
    private bool replacementRequested;
    private bool menuOpenRequested;
    private uint fishingLogCommandId;
    private delegate void ExecuteMainCommandDelegate(nint module, uint command);
    private Hook<ExecuteMainCommandDelegate>? mainCommandHook;

    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IAddonLifecycle AddonLifecycle { get; set; } = null!;
    [PluginService] private static IAddonEventManager AddonEvents { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInteropProvider { get; set; } = null!;
    [PluginService] private static IKeyState KeyState { get; set; } = null!;

    public Plugin()
    {
        data = LoadData();
        if (File.Exists(DataCachePath))
            dataRefreshStatus = $"Cached data: {File.GetLastWriteTime(DataCachePath):g}";
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.ApplySimplifiedJournalSettings();
        if (configuration.Version < 9)
        {
            // The area column was narrowed to leave more room for the fish
            // panel; existing saves would otherwise keep the old width.
            configuration.NativeAreaWidth = 280.0f;
            configuration.Version = 9;
        }
        if (configuration.Version < 10)
        {
            // The area dropdowns now stretch to nearly fill the column
            // instead of leaving a wide, uneven gap on either side.
            configuration.NativeAreaDropdownWidth = 999.0f;
            configuration.Version = 10;
        }
        PluginInterface.SavePluginConfig(configuration);
        regionByZone = data.Info.Values
            .Where(info => !string.IsNullOrWhiteSpace(info.Zone) && !string.IsNullOrWhiteSpace(info.Region))
            .GroupBy(info => info.Zone, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.GroupBy(info => info.Region)
                .OrderByDescending(regions => regions.Count()).First().Key, StringComparer.OrdinalIgnoreCase);
        nativeUiInitialization = KamiToolKitLibrary.InitializeAsync(PluginInterface, "Fishing Clues");
        dalamudJournal = new DalamudJournalWindow(GetJournal, OpenFish, OpenNormalFishingLog, TextureProvider, configuration, BuildFishSection,
            () => PluginInterface.SavePluginConfig(configuration));
        dalamudClues = new DalamudClueWindow();
        dalamudSettings = new DalamudSettingsWindow(configuration, SaveConfiguration, ApplyLiveNativeLayout, LogJournalDiagnostics, () => { _ = RefreshFishDataAsync(); }, () => dataRefreshBusy, () => dataRefreshStatus, KeyState);
        diagnosticWindow = new DiagnosticWindow(LogJournalDiagnostics);
        windowSystem.AddWindow(diagnosticWindow);
        windowSystem.AddWindow(dalamudJournal);
        windowSystem.AddWindow(dalamudClues);
        windowSystem.AddWindow(dalamudSettings);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the native Fishing Clues journal.\n/fishingclues settings → Open settings.",
        });
        ContextMenu.OnMenuOpened += OnMenuOpened;
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FishingNote", OnFishingNoteIntercept);
        AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "FishingNote", OnFishingNoteIntercept);
        AddonLifecycle.RegisterListener(AddonEvent.PostHide, "FishingNote", OnNormalLogClosed);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FishingNote", OnNormalLogClosed);
        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenMainUi += OpenJournal;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        InitializeMainCommandHook();
    }

    private unsafe void InitializeMainCommandHook()
    {
        try
        {
            fishingLogCommandId = DataManager.GetExcelSheet<MainCommandSheet>()
                .FirstOrDefault(row => row.Name.ToString().Equals("Fishing Log", StringComparison.OrdinalIgnoreCase)).RowId;
            UIModuleInterface* module = (UIModuleInterface*)UIModule.Instance();
            if (fishingLogCommandId == 0 || module == null) return;
            mainCommandHook = GameInteropProvider.HookFromAddress<ExecuteMainCommandDelegate>(
                (nint)module->VirtualTable->ExecuteMainCommand, ExecuteMainCommandDetour);
            mainCommandHook.Enable();
        }
        catch (Exception ex) { Log.Error(ex, "Fishing Log menu interception unavailable; using addon interception."); }
    }

    private void ExecuteMainCommandDetour(nint module, uint command)
    {
        if (!allowExplicitVanillaLog && configuration.ReplaceNormalFishingLog && command == fishingLogCommandId)
        {
            menuOpenRequested = true;
            return;
        }
        mainCommandHook!.Original(module, command);
    }

    public unsafe void Dispose()
    {
        // Note whether our custom journal is currently standing in for the
        // normal Fishing Log, so it can be restored once our interception
        // hook is gone below.
        bool wasReplacingLog = configuration.ReplaceNormalFishingLog && nativeJournal?.IsOpen == true;
        disposed = true;
        ReleaseItemMenuData();
        refreshCancellation.Cancel();
        mainCommandHook?.Dispose();
        CommandManager.RemoveHandler(CommandName);
        ContextMenu.OnMenuOpened -= OnMenuOpened;
        AddonLifecycle.UnregisterListener(OnFishingNoteIntercept);
        AddonLifecycle.UnregisterListener(OnNormalLogClosed);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenJournal;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        windowSystem.RemoveAllWindows();
        if (nativeUiInitialization.IsCompletedSuccessfully)
        {
            nativeJournal?.Dispose();
            nativeGuide?.Dispose();
            nativeClues?.Dispose();
            KamiToolKitLibrary.Dispose();
        }
        if (wasReplacingLog && fishingLogCommandId != 0)
        {
            // The interception hook above is already gone, so this reaches
            // the game's own Fishing Log implementation directly.
            UIModuleInterface* module = (UIModuleInterface*)UIModule.Instance();
            if (module != null) module->ExecuteMainCommand(fishingLogCommandId);
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase))
            OpenSettings();
        else if (arguments.Trim().Equals("diagnostics", StringComparison.OrdinalIgnoreCase))
            LogJournalDiagnostics();
        else
            OpenJournal();
    }

    private unsafe void LogJournalDiagnostics()
    {
        var report = new StringBuilder();
        report.AppendLine($"Fishing Clues {typeof(Plugin).Assembly.GetName().Version}");
        report.AppendLine($"Client structs: {typeof(PlayerState).Assembly.GetName().Version}");
        report.AppendLine($"Normal-log return: {normalLogReturnStatus}");
        report.AppendLine($"Replacement={configuration.ReplaceNormalFishingLog}; loggedIn={ClientState.IsLoggedIn}; explicit={allowExplicitVanillaLog}; seenVisible={normalLogWasVisible}; closeQueued={normalLogCloseRequested}; customOpen={nativeJournal?.IsOpen == true}");
        try
        {
        PlayerState* player = PlayerState.Instance();
        AgentFishingNote* agent = AgentFishingNote.Instance();
        report.AppendLine($"Player available={player != null}; agent available={agent != null}");
        if (player != null)
        {
            var flags = player->UnlockedFishingSpotsBitArray;
            report.AppendLine($"Discovery field offset=0x{(long)(flags.Pointer - (byte*)player):X}; bits={flags.BitCount}");
            report.AppendLine($"Discovery bytes={Convert.ToHexString(new ReadOnlySpan<byte>(flags.Pointer, flags.ByteLength))}");
        }
        if (agent != null)
        {
            report.AppendLine($"Mode={agent->Mode}; regionCount={agent->RegionCount}; selectedRegion={agent->SelectedRegionPlaceNameId}; selectedSpot={agent->SelectedSpotIndex}");
            report.AppendLine($"ViewingRegion={agent->ViewingPlaceNameRegionId}; viewingPlace={agent->ViewingPlaceNameId}");
            for (int i = 0; i < Math.Min((int)agent->RegionCount, agent->RegionPlaceNameIds.Length); i++)
                report.AppendLine($"Native region[{i}]={agent->RegionPlaceNameIds[i]}");
            for (int i = 0; i < agent->Spots.Length; i++)
            {
                var entry = agent->Spots[i];
                report.AppendLine($"Native entry[{i}]: order={entry.Order}, place={entry.PlaceNameId}, map={entry.MapId}");
            }
        }
        foreach (FishingSpotSheet spot in DataManager.GetExcelSheet<FishingSpotSheet>())
        {
            if (spot.PlaceName.RowId == 0) continue;
            // IDs and flags only: diagnostics must not reveal unknown names.
            string discovered = player == null ? "unavailable"
                : spot.RowId >= player->UnlockedFishingSpotsBitArray.BitCount ? "out-of-range"
                : IsFishingHoleDiscovered(player, spot.RowId).ToString();
            report.AppendLine($"Hole row={spot.RowId}, order={spot.Order}, main={spot.PlaceNameMain.RowId}, sub={spot.PlaceNameSub.RowId}, place={spot.PlaceName.RowId}, discovered={discovered}");
        }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Report failed: {ex.GetType().Name}: {ex.Message}");
            Log.Error(ex, "Could not capture all fishing diagnostics.");
        }
        diagnosticWindow.Show(report.ToString());
    }

    private void OnDraw()
    {
        windowSystem.Draw();
        DrawItemMenu();
        if (configuration.OpenUnknownOnLeftClick && !configuration.ReplaceNormalFishingLog)
            TryHandleUnknownFishClick();
    }

    private static uint pendingItemMenu;
    private System.Runtime.InteropServices.GCHandle itemLinkPayload;
    private nint itemLinkData;
    private uint openingLinkedItem;
    internal static void RequestItemMenu(uint itemId) => pendingItemMenu = itemId;
    private unsafe void DrawItemMenu()
    {
        if (pendingItemMenu == 0) return;
        uint itemId = pendingItemMenu;
        pendingItemMenu = 0;
        var panelPtr = GameGui.GetAddonByName("ChatLogPanel_0");
        if (panelPtr.IsNull) { Log.Warning("Cannot open native item menu: chat panel is unavailable."); return; }
        var panel = (AddonChatLogPanel*)panelPtr.Address;
        if (panel->LogViewer.ChatText == null) return;
        ReleaseItemMenuData();
        var payload = new Dalamud.Game.Text.SeStringHandling.SeString(
            new Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload(itemId, false),
            new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(ItemName(itemId)),
            Dalamud.Game.Text.SeStringHandling.Payloads.RawPayload.LinkTerminator).Encode();
        itemLinkPayload = System.Runtime.InteropServices.GCHandle.Alloc(payload, System.Runtime.InteropServices.GCHandleType.Pinned);
        itemLinkData = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(LinkData));
        var link = (LinkData*)itemLinkData;
        *link = new LinkData {
            LinkType = (byte)Lumina.Text.Payloads.LinkMacroPayloadType.Item,
            UIntValue1 = itemId,
            Payload = (byte*)itemLinkPayload.AddrOfPinnedObject(),
            PayloadEnd = checked((ushort)payload.Length),
        };
        // Use the actual chat handler. It builds the game's menu and invokes
        // the normal context-menu hooks, including other installed plugins.
        openingLinkedItem = itemId;
        try { panel->LogViewer.HandleLinkClick(link); }
        finally { openingLinkedItem = 0; }
    }
    private void ReleaseItemMenuData()
    {
        if (itemLinkPayload.IsAllocated) itemLinkPayload.Free();
        if (itemLinkData != 0) System.Runtime.InteropServices.Marshal.FreeHGlobal(itemLinkData);
        itemLinkData = 0;
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (configuration.AutoRefreshFishData && DateTime.UtcNow >= nextDataCheck)
        {
            nextDataCheck = DateTime.UtcNow.AddDays(1);
            if (!File.Exists(DataCachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(DataCachePath) >= TimeSpan.FromDays(1))
                _ = RefreshFishDataAsync();
        }

        ObserveVanillaRegionLabels();
        bool journalOpenNow = nativeJournal?.IsOpen == true;
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
        if (configuration.JournalKeybindEnabled && configuration.JournalKeybindKey != 0 && !configuration.ReplaceNormalFishingLog)
        {
            var key = (VirtualKey)configuration.JournalKeybindKey;
            bool down = KeyState.IsVirtualKeyValid(key) && KeyState[key]
                && (!configuration.JournalKeybindCtrl || KeyState[VirtualKey.CONTROL])
                && (!configuration.JournalKeybindAlt || KeyState[VirtualKey.MENU])
                && (!configuration.JournalKeybindShift || KeyState[VirtualKey.SHIFT]);
            if (down && !journalKeybindWasDown && ClientState.IsLoggedIn)
                OpenJournal();
            journalKeybindWasDown = down;
        }
        else journalKeybindWasDown = false;
        if (menuOpenRequested)
        {
            menuOpenRequested = false;
            replacementRequested = false;
            pendingNormalLogSpot = null;
            allowExplicitVanillaLog = false;
            normalLogWasVisible = false;
            normalLogCloseRequested = false;
            normalLogReturnPending = false;
            AgentFishingNote* normalLog = AgentFishingNote.Instance();
            if (normalLog != null && ((AgentInterface*)normalLog)->IsAgentActive())
                ((AgentInterface*)normalLog)->Hide();
            lastReplacement = Environment.TickCount64;
            ToggleReplacementJournal();
            return;
        }
        ApplyPendingNormalLogSelection();
        long now = Environment.TickCount64;
        AgentFishingNote* agent = AgentFishingNote.Instance();
        AtkUnitBasePtr ptr = GameGui.GetAddonByName("FishingNote");
        bool agentActive = agent != null && ((AgentInterface*)agent)->IsAgentActive();
        bool addonVisible = !ptr.IsNull && ptr.IsVisible;
        if (normalLogReturnPending)
        {
            replacementRequested = false;
            normalLogCloseRequested = false;
            if (!configuration.ReplaceNormalFishingLog || !ClientState.IsLoggedIn)
            {
                normalLogReturnPending = false;
                normalLogReturnStatus = "Pending return cancelled: replacement disabled or logged out.";
            }
            else if (now >= normalLogReturnAfter && !addonVisible)
            {
                // Close(false) is asynchronous. Do not create another addon with
                // the same name until the previous instance has been removed.
                var previous = GameGui.GetAddonByName("FishingCluesJournalNative");
                if (previous.IsNull)
                {
                    normalLogReturnPending = false;
                    lastReplacement = now;
                    _ = ReturnFromNormalLogAsync();
                }
                else normalLogReturnStatus = "Waiting for previous custom journal to finish closing.";
            }
            return;
        }
        bool normalClosed = normalLogCloseRequested || (normalLogWasVisible && !addonVisible);
        if (addonVisible && (allowExplicitVanillaLog || !configuration.ReplaceNormalFishingLog))
            normalLogWasVisible = true;
        if (normalClosed)
        {
            normalLogCloseRequested = false;
            normalLogWasVisible = false;
            allowExplicitVanillaLog = false;
            replacementRequested = false;
            pendingNormalLogSpot = null;
            // The low-level PlayerState.IsLoaded field is not a reliable login
            // gate on the user's client build. Use the supported service instead.
            if (configuration.ReplaceNormalFishingLog && ClientState.IsLoggedIn)
            {
                if (agentActive) ((AgentInterface*)agent)->Hide();
                lastReplacement = now;
                normalLogReturnStatus = "Close observed; waiting for window teardown.";
                normalLogReturnPending = true;
                normalLogReturnAfter = now + 150;
                CloseReplacementJournal();
            }
            else normalLogReturnStatus = $"Close observed; skipped (replacement={configuration.ReplaceNormalFishingLog}, loggedIn={ClientState.IsLoggedIn}).";
            return;
        }
        if (allowExplicitVanillaLog)
        {
            // This is the lifetime of the user's normal-log visit, not a
            // five-second selection request. Only an actual close ends it.
            return;
        }
        if (replacementRequested)
        {
            replacementRequested = false;
            lastReplacement = now;
            ToggleReplacementJournal();
            return;
        }
        if (!configuration.ReplaceNormalFishingLog || now - lastReplacement < 1000)
            return;
        if (!agentActive && !addonVisible)
            return;
        lastReplacement = now;
        if (agentActive)
            ((AgentInterface*)agent)->Hide();
        if (addonVisible)
        {
            var addon = (AtkUnitBase*)ptr.Address;
            if (addon != null && addon->IsReady)
                addon->Close(true);
        }
        ToggleReplacementJournal();
    }

    private void SaveConfiguration()
    {
        PluginInterface.SavePluginConfig(configuration);
        if (configuration.EmbedFishDetails)
        {
            dalamudClues.IsOpen = false;
            nativeClues?.Close();
        }
    }

    private void ApplyLiveNativeLayout()
    {
        if (nativeJournal?.IsOpen != true)
            return;
        nativeJournal.ApplyLayout(
            Math.Clamp(configuration.NativeWindowWidth, 850.0f, 1600.0f),
            Math.Clamp(configuration.NativeWindowHeight, 450.0f, 1000.0f),
            configuration.NativeRegionWidth,
            configuration.NativeAreaWidth,
            configuration.NativeAreaDropdownWidth,
            configuration.ShowOpenNormalLogButton);
    }

    private unsafe void OnFishingNoteIntercept(AddonEvent eventType, AddonArgs args)
    {
        if (normalLogReturnPending) return;
        if (!configuration.ReplaceNormalFishingLog || allowExplicitVanillaLog)
            return;
        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null)
            return;

        replacementRequested = true;
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (agent != null && ((AgentInterface*)agent)->IsAgentActive())
            ((AgentInterface*)agent)->Hide();
        addon->Close(true);
    }

    private void OnNormalLogClosed(AddonEvent eventType, AddonArgs args)
    {
        // Queue until Framework.Update, outside the game's hide/finalize callback.
        // Ignore log windows closed by our initial replacement interception.
        if (allowExplicitVanillaLog || normalLogWasVisible)
        {
            normalLogCloseRequested = true;
            normalLogReturnStatus = $"Received {eventType}; return queued.";
        }
    }

    private async Task ReturnFromNormalLogAsync()
    {
        // Recreate the custom addon through the established open path, which
        // saves/restores session state even if the old native instance lingers.
        await OpenNativeJournalAsync();
        normalLogReturnStatus = nativeJournal?.IsOpen == true
            ? "Custom journal reopened."
            : "Open requested, but custom addon is not visible; check plugin log for setup errors.";
    }

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

    private static uint GetFishingLogIconId()
    {
        foreach (MainCommandSheet action in DataManager.GetExcelSheet<MainCommandSheet>())
        {
            if (action.Name.ToString().Equals("Fishing Log", StringComparison.OrdinalIgnoreCase))
                return (uint)action.Icon;
        }
        return 0;
    }

    private unsafe void OpenNormalFishingLog()
    {
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (agent == null)
            return;
        CloseReplacementJournal();
        normalLogReturnPending = false;
        normalLogCloseRequested = false;
        allowExplicitVanillaLog = true;
        normalLogReturnStatus = "Normal log explicitly opened; waiting for close.";
        pendingNormalLogUntil = Environment.TickCount64 + 5000;
        pendingNormalLogSelectionAppliedAt = 0;
        pendingNormalLogSpot = null;
        replacementRequested = false;
        // The main command initializes the fishing notebook data. Agent.Show alone
        // can display an empty shell when the command was previously intercepted.
        if (fishingLogCommandId != 0) {
            UIModuleInterface* module = (UIModuleInterface*)UIModule.Instance();
            if (module != null) module->ExecuteMainCommand(fishingLogCommandId);
        } else ((AgentInterface*)agent)->Show();
    }

    private unsafe static void ConfigureNormalLogRegion(AgentFishingNote* agent, JournalSpot spot)
    {
        bool switchingRegion = agent->SelectedRegionPlaceNameId != spot.RegionPlaceNameId;
        agent->Mode = 0;
        agent->SelectedRegionPlaceNameId = spot.RegionPlaceNameId;
        agent->ViewingPlaceNameRegionId = spot.RegionPlaceNameId;
        int regionCount = Math.Min((int)agent->RegionCount, agent->RegionPlaceNameIds.Length);
        for (int i = 0; i < regionCount; i++)
        {
            if (agent->RegionPlaceNameIds[i] != spot.RegionPlaceNameId)
                continue;
            agent->SelectedRegionIndex = (ushort)i;
            break;
        }
        if (!switchingRegion)
            return;
        agent->SelectedSpotIndex = -1;
        agent->ViewingPlaceNameId = 0;
        agent->FishSlotsDirty = true;
    }

    private unsafe void ApplyPendingNormalLogSelection()
    {
        JournalSpot? spot = pendingNormalLogSpot;
        if (spot is null)
            return;
        if (Environment.TickCount64 > pendingNormalLogUntil)
        {
            pendingNormalLogSpot = null;
            pendingNormalLogSelectionAppliedAt = 0;
            return;
        }
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (agent == null)
            return;

        ConfigureNormalLogRegion(agent, spot);

        for (int i = 0; i < agent->Spots.Length; i++)
        {
            AgentFishingNote.SpotEntry nativeSpot = agent->Spots[i];
            if (nativeSpot.Order != spot.Order)
                continue;
            agent->SelectedSpotIndex = (short)i;
            agent->ViewingPlaceNameId = nativeSpot.PlaceNameId != 0 ? nativeSpot.PlaceNameId : spot.SpotPlaceNameId;
            agent->FishSlotsDirty = true;
            if (pendingNormalLogSelectionAppliedAt == 0)
                pendingNormalLogSelectionAppliedAt = Environment.TickCount64;
            else if (Environment.TickCount64 - pendingNormalLogSelectionAppliedAt >= 500)
            {
                pendingNormalLogSpot = null;
                pendingNormalLogSelectionAppliedAt = 0;
            }
            return;
        }
    }

    private void CloseReplacementJournal()
    {
        dalamudJournal.IsOpen = false;
        if (nativeJournal?.IsOpen == true)
            nativeJournal.Close();
    }

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
                entries.Add(new JournalFish(fishId, itemId, spot.GatheringLevel, IsCaught(caught, fishId), fishName, icon, info, spot.RowId,
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
            bool isUnlocked = IsFishingHoleDiscovered(player, spot.RowId);
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

    private unsafe void OnMenuOpened(IMenuOpenedArgs args)
    {
        uint contextItem = args.Target switch {
            MenuTargetInventory inventory => inventory.TargetItem?.BaseItemId ?? 0,
            MenuTargetDefault => (uint)GameGui.HoveredItem,
            _ => 0,
        };
        if (openingLinkedItem != 0) contextItem = openingLinkedItem;
        contextItem %= 500000;
        if (contextItem == 0 && args.Target is MenuTargetInventory) {
            var inventoryContext = AgentInventoryContext.Instance();
            if (inventoryContext != null && inventoryContext->TargetInventorySlot != null)
                contextItem = inventoryContext->TargetInventorySlot->GetBaseItemId();
        }
        if (contextItem != 0 && (data.Locations.Any(l => l.ItemId == contextItem) ||
            DataManager.GetExcelSheet<FishParameterSheet>().Any(f => f.Item.RowId == contextItem))) {
            string query = ItemName(contextItem);
            args.AddMenuItem(new MenuItem {
                Name = "Search Fishing Clues", PrefixChar = 'F', PrefixColor = 43,
                OnClicked = clicked => { _ = OpenGuideAsync(query, contextItem); },
            });
        }
        if (!string.Equals(args.AddonName, "FishingNote", StringComparison.OrdinalIgnoreCase))
            return;
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (agent == null || agent->Mode != 0 || agent->SelectedSpotIndex < 0 || agent->SelectedSpotIndex >= agent->Spots.Length)
            return;
        List<HiddenFish> missing = CaptureMissingFish(agent);
        args.AddMenuItem(new MenuItem
        {
            Name = missing.Count == 0 ? "Fishing Clues - all fish caught" : $"Fishing Clues - {missing.Count} uncaught",
            PrefixChar = 'F', PrefixColor = 43, IsEnabled = missing.Count > 0,
            OnClicked = clicked => OpenClues(missing),
        });
    }

    private unsafe List<HiddenFish> CaptureMissingFish(AgentFishingNote* agent)
    {
        var result = new List<HiddenFish>();
        int count = Math.Min(agent->FishSlotCount, (byte)agent->FishSlots.Length);
        var fishSheet = DataManager.GetExcelSheet<FishParameterSheet>();
        var itemSheet = DataManager.GetExcelSheet<ItemSheet>();
        AtkUnitBasePtr ptr = GameGui.GetAddonByName("FishingNote");
        AtkUnitBase* addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        for (int i = 0; i < count; i++)
        {
            AgentFishingNote.FishSlot slot = agent->FishSlots[i];
            if (slot.Id == 0 || slot.IsCaught || !fishSheet.TryGetRow(slot.Id, out FishParameterSheet fish))
                continue;
            uint itemId = fish.Item.RowId;
            string itemName = itemSheet.TryGetRow(itemId, out ItemSheet item) ? item.Name.ToString() : string.Empty;
            string? knownName = !string.IsNullOrWhiteSpace(itemName) && IsNameVisibleInFishingLog(addon, itemName) ? itemName : null;
            result.Add(new HiddenFish(slot.Id, itemId, fish.FishingSpot.IsValid ? fish.FishingSpot.Value.GatheringLevel : (byte)0, knownName));
        }
        return result;
    }

    private unsafe void TryHandleUnknownFishClick()
    {
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left) || Environment.TickCount64 - lastClickHandled < 250)
            return;
        AtkUnitBasePtr ptr = GameGui.GetAddonByName("FishingNote");
        if (ptr.IsNull || !ptr.IsVisible)
            return;
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (agent == null || agent->Mode != 0)
            return;
        var missing = CaptureMissingFish(agent).Where(f => f.KnownName is null).ToList();
        if (missing.Count == 0)
            return;
        var unknownNodes = new List<nint>();
        AtkUnitBase* addon = (AtkUnitBase*)ptr.Address;
        FindUnknownTextNodes(&addon->UldManager, unknownNodes, 0);
        unknownNodes.Sort((a, b) =>
        {
            var left = (AtkTextNode*)a;
            var right = (AtkTextNode*)b;
            int y = left->AtkResNode.ScreenY.CompareTo(right->AtkResNode.ScreenY);
            return y != 0 ? y : left->AtkResNode.ScreenX.CompareTo(right->AtkResNode.ScreenX);
        });
        Vector2 mouse = ImGui.GetMousePos();
        for (int i = 0; i < Math.Min(unknownNodes.Count, missing.Count); i++)
        {
            AtkResNode* node = &((AtkTextNode*)unknownNodes[i])->AtkResNode;
            float width = Math.Max(node->Width * node->GetScaleX(), 90.0f);
            float height = Math.Max(node->Height * node->GetScaleY(), 32.0f);
            float left = node->ScreenX - 12.0f;
            float top = node->ScreenY - 8.0f;
            if (mouse.X < left || mouse.X > left + width + 24.0f || mouse.Y < top || mouse.Y > top + height + 16.0f)
                continue;
            lastClickHandled = Environment.TickCount64;
            OpenClues([missing[i]]);
            return;
        }
    }

    private unsafe static void FindUnknownTextNodes(AtkUldManager* manager, List<nint> result, int depth)
    {
        if (manager == null || manager->NodeList == null || depth > 12) return;
        for (int i = 0; i < manager->NodeListCount; i++)
        {
            AtkResNode* node = manager->NodeList[i];
            if (node == null || !node->IsVisible()) continue;
            if (node->Type == NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                string value = text->NodeText.ToString().Trim();
                if (value.Length >= 2 && value.All(c => c == '?' || char.IsWhiteSpace(c))) result.Add((nint)text);
            }
            else if (node->Type == NodeType.Component)
            {
                AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
                if (component != null) FindUnknownTextNodes(&component->UldManager, result, depth + 1);
            }
        }
    }

    private void OpenFish(JournalFish fish)
        => OpenSections([BuildFishSection(fish)]);

    private FishClueSection BuildFishSection(JournalFish fish)
    {
        var lines = new List<string>();
        if (fish.IdentityVisible && (data.Info.GetValueOrDefault(fish.ItemId) ?? fish.Info) is { } info)
        {
            if (!string.IsNullOrWhiteSpace(info.Waters)) lines.Add($"Waters: {info.Waters}");
            if (!string.IsNullOrWhiteSpace(info.Region) || !string.IsNullOrWhiteSpace(info.Zone)) lines.Add($"Location: {info.Region} - {info.Zone}");
            if (info.Collectable) lines.Add("Collectable: Yes");
            if (!string.IsNullOrWhiteSpace(info.Description)) lines.Add($"Description: {info.Description}");
        }
        lines.AddRange(BuildRequirementLines(fish.ItemId, fish.SpotId));
        string heading = fish.IdentityVisible ? fish.Name : "????";
        if (fish.Level > 0) heading += $"   Lv. {fish.Level}";
        return new FishClueSection(heading, lines);
    }

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

    private IReadOnlyList<string> BuildRequirementLines(uint itemId, uint spotId = 0)
    {
        var lines = new List<string>();
        lines.AddRange(BuildBaitLines(itemId, spotId));
        if (!data.Fish.TryGetValue(itemId, out FishCondition? condition)) {
            lines.Add("Requirements unknown."); return lines;
        }
        if (!condition.RequirementsKnown) lines.Add("Some requirements are unknown.");
        if (condition.StartHour != 0 || condition.EndHour != 24)
            lines.Add($"Time: {FormatTime(condition.StartHour, condition.EndHour)}");
        if (condition.Weather.Count > 0)
            lines.Add($"Weather: {FormatWeather(condition.Weather, "No special requirement")}");
        if (condition.PreviousWeather.Count > 0) lines.Add($"Previous weather: {FormatWeather(condition.PreviousWeather, "None")}");
        if (condition.RequirementsKnown && condition.StartHour == 0 && condition.EndHour == 24 &&
            condition.Weather.Count == 0 && condition.PreviousWeather.Count == 0 && condition.Predators.Count == 0 &&
            condition.Folklore is null && condition.Snagging != true && string.IsNullOrWhiteSpace(condition.Lure))
            lines.Add("No special requirements.");
        foreach (List<uint> predator in condition.Predators)
            if (predator.Count >= 2) lines.Add($"Intuition: catch {predator[1]} x {ItemName(predator[0])}");
        if (condition.IntuitionSeconds is int seconds && seconds > 0) lines.Add($"Intuition window: {seconds / 60}:{seconds % 60:00}");
        if (condition.Folklore is uint folklore) lines.Add($"Folklore: {data.Folklore.GetValueOrDefault(folklore, $"Book #{folklore}")}");
        if (condition.FishEyes == true) lines.Add("Fish Eyes: supported");
        if (condition.Snagging == true) lines.Add("Snagging: required");
        if (!string.IsNullOrWhiteSpace(condition.Lure)) lines.Add($"Lure: {condition.Lure}");
        if (!string.IsNullOrWhiteSpace(condition.Gig)) lines.Add($"Spear shadow size: {condition.Gig}");
        if (!string.IsNullOrWhiteSpace(condition.SpearSpeed)) lines.Add($"Spear movement speed: {condition.SpearSpeed}");
        if (!string.IsNullOrWhiteSpace(condition.Tug) || !string.IsNullOrWhiteSpace(condition.Hookset)) lines.Add($"Hook: {FormatHook(condition)}");
        return lines;
    }

    private IReadOnlyList<string> BuildBaitLines(uint itemId, uint spotId)
    {
        var lines = new List<string>();
        if (data.Locations.Any(l => l.ItemId == itemId && l.Spearfishing) ||
            (data.Fish.TryGetValue(itemId, out var condition) && !string.IsNullOrEmpty(condition.Gig)))
            return ["Method: Spearfishing - no bait required."];
        if (spotId == 0) return [];
        if (!data.SpotBaits.TryGetValue(itemId, out var spots) || !spots.TryGetValue(spotId, out var entry))
            return ["Baits for this fishing hole: unknown."];
        bool IsFish(uint id) => data.Info.ContainsKey(id) || data.Fish.ContainsKey(id);
        var direct = entry.Recommended.Concat(entry.Observed).Distinct().Where(id => !IsFish(id)).ToArray();
        var mooch = entry.Recommended.Concat(entry.Observed).Distinct().Where(IsFish).ToArray();
        var recommended = direct.Where(entry.Recommended.Contains).ToArray();
        var reported = direct.Where(id => !entry.Recommended.Contains(id)).ToArray();
        if (recommended.Length > 0) lines.Add($"Bait: {string.Join(" / ", recommended.Select(ItemName))}");
        if (reported.Length > 0) lines.Add($"Other reported baits: {string.Join(" / ", reported.Select(ItemName))}");
        if (mooch.Length > 0) {
            lines.Add($"Mooch from: {string.Join(" / ", mooch.Select(ItemName))}");
            foreach (var id in mooch) AppendMooch(id, spotId, new HashSet<uint> { itemId }, 1, lines);
        }
        if (direct.Length == 0 && mooch.Length == 0) lines.Add("Baits for this fishing hole: unknown.");
        if (entry.MinimumGathering > 0) lines.Add($"Minimum gathering: {entry.MinimumGathering}");

        return lines;
    }

    private void AppendMooch(uint fish, uint spot, HashSet<uint> visited, int depth, List<string> lines)
    {
        if (depth > 4 || !visited.Add(fish)) return;
        if (!data.SpotBaits.TryGetValue(fish, out var spots) || !spots.TryGetValue(spot, out var entry)) {
            lines.Add($"To catch {ItemName(fish)} here: requirements unknown."); return;
        }
        var baits = entry.Recommended.Concat(entry.Observed).Distinct().ToArray();
        lines.Add($"To catch {ItemName(fish)} here: {string.Join(" / ", baits.Select(ItemName))}");
        foreach (var bait in baits.Where(id => data.Info.ContainsKey(id) || data.Fish.ContainsKey(id)))
            AppendMooch(bait, spot, new HashSet<uint>(visited), depth + 1, lines);
    }

    private string ItemName(uint id) => DataManager.GetExcelSheet<ItemSheet>().TryGetRow(id, out var item) ? item.Name.ToString() : data.Items.GetValueOrDefault(id, $"Item #{id}");
    private string FormatWeather(List<uint> ids, string fallback) => ids.Count == 0 ? fallback : string.Join(" / ", ids.Select(id => data.Weather.GetValueOrDefault(id, $"Weather #{id}")));
    private static string FormatTime(double start, double end)
    {
        if (start == 0 && end == 24) return "No special requirement (any time)";
        int startMinutes = (int)Math.Round(start * 60) % 1440;
        int endMinutes = (int)Math.Round(end * 60) % 1440;
        return $"{startMinutes / 60:00}:{startMinutes % 60:00}-{endMinutes / 60:00}:{endMinutes % 60:00} ET";
    }
    private static string FormatHook(FishCondition condition)
    {
        string marks = condition.Tug?.ToLowerInvariant() switch { "light" => "!", "medium" => "!!", "heavy" or "legendary" => "!!!", _ => "Unknown bite" };
        return string.IsNullOrWhiteSpace(condition.Hookset) ? marks : $"{marks} - {condition.Hookset} Hookset";
    }

    private unsafe static bool IsNameVisibleInFishingLog(AtkUnitBase* addon, string itemName) => addon != null && FindVisibleText(&addon->UldManager, itemName, 0);
    private unsafe static bool FindVisibleText(AtkUldManager* manager, string expected, int depth)
    {
        if (manager == null || manager->NodeList == null || depth > 12) return false;
        for (int i = 0; i < manager->NodeListCount; i++)
        {
            AtkResNode* node = manager->NodeList[i];
            if (node == null || !node->IsVisible()) continue;
            if (node->Type == NodeType.Text && ((AtkTextNode*)node)->NodeText.ToString().Trim().Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
            if (node->Type == NodeType.Component)
            {
                AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
                if (component != null && FindVisibleText(&component->UldManager, expected, depth + 1)) return true;
            }
        }
        return false;
    }
}
