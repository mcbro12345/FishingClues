using Dalamud.Game.NativeWrapper;
using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MainCommandSheet = Lumina.Excel.Sheets.MainCommand;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Models;
using FishingClues.UI;

namespace FishingClues.Game.Logic;

// Intercepts the vanilla Fishing Log - both the menu command and the addon
// itself - and swaps it for the native journal, then hands control back
// when the player explicitly asks for the real thing.
public sealed class NormalLogReplacementController
{
    private readonly Configuration configuration;
    private readonly WindowManager windowManager;

    private delegate void ExecuteMainCommandDelegate(nint module, uint command);
    private Hook<ExecuteMainCommandDelegate>? mainCommandHook;
    private uint fishingLogCommandId;

    private bool allowExplicitVanillaLog;
    private bool menuOpenRequested;
    private bool replacementRequested;
    private JournalSpot? pendingNormalLogSpot;
    private long pendingNormalLogUntil;
    private long pendingNormalLogSelectionAppliedAt;
    private bool normalLogWasVisible;
    private bool normalLogCloseRequested;
    private bool normalLogReturnPending;
    private long normalLogReturnAfter;
    private long lastReplacement;

    public string ReturnStatus { get; private set; } = "No normal-log close observed.";
    public bool AllowExplicitVanillaLog => allowExplicitVanillaLog;
    public bool WasNormalLogVisible => normalLogWasVisible;
    public bool IsCloseQueued => normalLogCloseRequested;

    public NormalLogReplacementController(Configuration configuration, WindowManager windowManager, JournalBuilder journal)
    {
        this.configuration = configuration;
        this.windowManager = windowManager;
        journal.CharacterChanged += () =>
        {
            normalLogReturnPending = false;
            normalLogWasVisible = false;
            normalLogCloseRequested = false;
        };
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FishingNote", OnFishingNoteIntercept);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "FishingNote", OnFishingNoteIntercept);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostHide, "FishingNote", OnNormalLogClosed);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "FishingNote", OnNormalLogClosed);
        InitializeMainCommandHook();
    }

    public void Dispose()
    {
        mainCommandHook?.Dispose();
        Services.AddonLifecycle.UnregisterListener(OnFishingNoteIntercept);
        Services.AddonLifecycle.UnregisterListener(OnNormalLogClosed);
    }

    // called after the hook above is disposed, so this reaches the game's own handler
    public unsafe void RestoreVanillaLog()
    {
        if (fishingLogCommandId == 0) return;
        UIModuleInterface* module = (UIModuleInterface*)UIModule.Instance();
        if (module != null) module->ExecuteMainCommand(fishingLogCommandId);
    }

    private unsafe void InitializeMainCommandHook()
    {
        try
        {
            fishingLogCommandId = Services.DataManager.GetExcelSheet<MainCommandSheet>()
                .FirstOrDefault(row => row.Name.ToString().Equals("Fishing Log", StringComparison.OrdinalIgnoreCase)).RowId;
            UIModuleInterface* module = (UIModuleInterface*)UIModule.Instance();
            if (fishingLogCommandId == 0 || module == null) return;
            mainCommandHook = Services.GameInteropProvider.HookFromAddress<ExecuteMainCommandDelegate>(
                (nint)module->VirtualTable->ExecuteMainCommand, ExecuteMainCommandDetour);
            mainCommandHook.Enable();
        }
        catch (Exception ex) { Services.Log.Error(ex, "Fishing Log menu interception unavailable; using addon interception."); }
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

    public unsafe void OnFrameworkUpdate()
    {
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
            windowManager.ToggleNativeJournal();
            return;
        }
        ApplyPendingNormalLogSelection();
        long now = Environment.TickCount64;
        AgentFishingNote* agent = AgentFishingNote.Instance();
        AtkUnitBasePtr ptr = Services.GameGui.GetAddonByName("FishingNote");
        bool agentActive = agent != null && ((AgentInterface*)agent)->IsAgentActive();
        bool addonVisible = !ptr.IsNull && ptr.IsVisible;
        if (normalLogReturnPending)
        {
            replacementRequested = false;
            normalLogCloseRequested = false;
            if (!configuration.ReplaceNormalFishingLog || !Services.ClientState.IsLoggedIn)
            {
                normalLogReturnPending = false;
                ReturnStatus = "Pending return cancelled: replacement disabled or logged out.";
            }
            else if (now >= normalLogReturnAfter && !addonVisible)
            {
                // Close(false) is async; wait for the old addon to actually go away
                var previous = Services.GameGui.GetAddonByName("FishingCluesJournalNative");
                if (previous.IsNull)
                {
                    normalLogReturnPending = false;
                    lastReplacement = now;
                    _ = ReturnFromNormalLogAsync();
                }
                else ReturnStatus = "Waiting for previous custom journal to finish closing.";
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
            if (configuration.ReplaceNormalFishingLog && Services.ClientState.IsLoggedIn)
            {
                if (agentActive) ((AgentInterface*)agent)->Hide();
                lastReplacement = now;
                ReturnStatus = "Close observed; waiting for window teardown.";
                normalLogReturnPending = true;
                normalLogReturnAfter = now + 150;
                windowManager.CloseNativeJournal();
            }
            else ReturnStatus = $"Close observed; skipped (replacement={configuration.ReplaceNormalFishingLog}, loggedIn={Services.ClientState.IsLoggedIn}).";
            return;
        }
        if (allowExplicitVanillaLog)
            return;
        if (replacementRequested)
        {
            replacementRequested = false;
            lastReplacement = now;
            windowManager.ToggleNativeJournal();
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
        windowManager.ToggleNativeJournal();
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
        // deferred to Framework.Update, outside the game's hide/finalize callback
        if (allowExplicitVanillaLog || normalLogWasVisible)
        {
            normalLogCloseRequested = true;
            ReturnStatus = $"Received {eventType}; return queued.";
        }
    }

    private async System.Threading.Tasks.Task ReturnFromNormalLogAsync()
    {
        await windowManager.OpenNativeJournalAsync();
        ReturnStatus = windowManager.IsNativeJournalOpen
            ? "Custom journal reopened."
            : "Open requested, but custom addon is not visible; check plugin log for setup errors.";
    }

    public unsafe void OpenNormalFishingLog()
    {
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (agent == null)
            return;
        windowManager.CloseNativeJournal();
        normalLogReturnPending = false;
        normalLogCloseRequested = false;
        allowExplicitVanillaLog = true;
        ReturnStatus = "Normal log explicitly opened; waiting for close.";
        pendingNormalLogUntil = Environment.TickCount64 + 5000;
        pendingNormalLogSelectionAppliedAt = 0;
        pendingNormalLogSpot = null;
        replacementRequested = false;
        // Agent.Show alone can leave an empty shell if the command was intercepted before
        if (fishingLogCommandId != 0)
        {
            UIModuleInterface* module = (UIModuleInterface*)UIModule.Instance();
            if (module != null) module->ExecuteMainCommand(fishingLogCommandId);
        }
        else ((AgentInterface*)agent)->Show();
    }

    private static unsafe void ConfigureNormalLogRegion(AgentFishingNote* agent, JournalSpot spot)
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
}
