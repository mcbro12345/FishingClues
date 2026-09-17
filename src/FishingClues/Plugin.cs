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
    private IReadOnlyList<JournalRegion>? journalCache;
    private long lastJournalBuild;
    private ulong journalCharacterId;
    private readonly HashSet<ushort> vanillaRevealedRegions = new();
    private long nextFishRevealScan;
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
            configuration.NativeAreaWidth = 280.0f;
            configuration.Version = 9;
        }
        if (configuration.Version < 10)
        {
            configuration.NativeAreaDropdownWidth = 999.0f;
            configuration.Version = 10;
        }
        if (configuration.Version < 11)
        {
            configuration.NativeAreaWidth = 350.0f;
            configuration.NativeAreaDropdownWidth = 310.0f;
            configuration.Version = 11;
        }
        if (configuration.Version < 12)
        {
            configuration.NativeRegionWidth = 162.0f;
            configuration.NativeAreaWidth = 263.0f;
            configuration.Version = 12;
        }
        if (configuration.Version < 13)
        {
            configuration.NativeAreaDropdownWidth = 999.0f;
            configuration.Version = 13;
        }
        PluginInterface.SavePluginConfig(configuration);
        regionByZone = data.Info.Values
            .Where(info => !string.IsNullOrWhiteSpace(info.Zone) && !string.IsNullOrWhiteSpace(info.Region))
            .GroupBy(info => info.Zone, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.GroupBy(info => info.Region)
                .OrderByDescending(regions => regions.Count()).First().Key, StringComparer.OrdinalIgnoreCase);
        nativeUiInitialization = KamiToolKitLibrary.InitializeAsync(PluginInterface, "Fishing Clues");
        dalamudClues = new DalamudClueWindow();
        dalamudSettings = new DalamudSettingsWindow(configuration, SaveConfiguration, ApplyLiveNativeLayout, LogJournalDiagnostics, () => { _ = RefreshFishDataAsync(); }, () => dataRefreshBusy, () => dataRefreshStatus, KeyState, RefreshJournalContents);
        diagnosticWindow = new DiagnosticWindow(LogJournalDiagnostics);
        windowSystem.AddWindow(diagnosticWindow);
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
            KamiToolKitLibrary.Dispose();
        }
        if (wasReplacingLog && fishingLogCommandId != 0)
        {
            // hook is already disposed above, so this reaches the game's own handler
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
        report.AppendLine($"Layout: regionWidth={configuration.NativeRegionWidth:0.#}; areaWidth={configuration.NativeAreaWidth:0.#}; areaDropdownWidth={configuration.NativeAreaDropdownWidth:0.#}");
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
            // IDs and flags only, never fish/spot names not yet unlocked
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
    }

    private void SaveConfiguration()
    {
        PluginInterface.SavePluginConfig(configuration);
        dalamudClues.IsOpen = false;
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

}
