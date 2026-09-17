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

public sealed partial class Plugin
{
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
                // Close(false) is async; wait for the old addon to actually go away
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
            return;
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
            normalLogReturnStatus = $"Received {eventType}; return queued.";
        }
    }

    private async Task ReturnFromNormalLogAsync()
    {
        await OpenNativeJournalAsync();
        normalLogReturnStatus = nativeJournal?.IsOpen == true
            ? "Custom journal reopened."
            : "Open requested, but custom addon is not visible; check plugin log for setup errors.";
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
        // Agent.Show alone can leave an empty shell if the command was intercepted before
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
        if (nativeJournal?.IsOpen == true)
            nativeJournal.Close();
    }
}
