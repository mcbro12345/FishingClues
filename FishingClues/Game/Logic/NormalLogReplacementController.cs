using System;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.NativeWrapper;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MainCommandSheet = Lumina.Excel.Sheets.MainCommand;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.UI;

namespace FishingClues.Game.Logic;

// Intercepts the vanilla Fishing Log, both the menu command and the addon
// itself, and swaps in the journal window. Hands control back when the player
// explicitly opens the real one, and returns to the journal when it closes.
public sealed class NormalLogReplacementController
{
    private const long ReplacementCooldownMs = 1000;
    private const long ReturnDelayMs = 150;

    private readonly Configuration configuration;
    private readonly WindowManager windowManager;

    private delegate void ExecuteMainCommandDelegate(nint module, uint command);
    private Hook<ExecuteMainCommandDelegate>? mainCommandHook;
    private uint fishingLogCommandId;

    private bool allowExplicitVanillaLog;
    private bool menuOpenRequested;
    private bool replacementRequested;
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

    // Called after the hook is disposed, so this reaches the game's own handler.
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
            HandleMenuRequest();
            return;
        }
        long now = Environment.TickCount64;
        AgentFishingNote* agent = AgentFishingNote.Instance();
        AtkUnitBasePtr ptr = Services.GameGui.GetAddonByName("FishingNote");
        bool agentActive = agent != null && ((AgentInterface*)agent)->IsAgentActive();
        bool addonVisible = !ptr.IsNull && ptr.IsVisible;

        if (normalLogReturnPending)
        {
            HandlePendingReturn(now, addonVisible);
            return;
        }
        bool normalClosed = normalLogCloseRequested || (normalLogWasVisible && !addonVisible);
        if (addonVisible && (allowExplicitVanillaLog || !configuration.ReplaceNormalFishingLog))
            normalLogWasVisible = true;
        if (normalClosed)
        {
            HandleNormalLogClosed(now, agent, agentActive);
            return;
        }
        if (allowExplicitVanillaLog)
            return;
        if (replacementRequested)
        {
            replacementRequested = false;
            ReplaceVanillaLog(now, agent, agentActive, ptr, addonVisible);
            return;
        }
        // The vanilla log opened some way the hooks missed.
        if (!configuration.ReplaceNormalFishingLog || now - lastReplacement < ReplacementCooldownMs)
            return;
        if (!agentActive && !addonVisible)
            return;
        ReplaceVanillaLog(now, agent, agentActive, ptr, addonVisible);
    }

    // The Fishing Log was picked from the game menu: open the journal instead.
    private unsafe void HandleMenuRequest()
    {
        menuOpenRequested = false;
        replacementRequested = false;
        allowExplicitVanillaLog = false;
        normalLogWasVisible = false;
        normalLogCloseRequested = false;
        normalLogReturnPending = false;
        AgentFishingNote* normalLog = AgentFishingNote.Instance();
        if (normalLog != null && ((AgentInterface*)normalLog)->IsAgentActive())
            ((AgentInterface*)normalLog)->Hide();
        lastReplacement = Environment.TickCount64;
        windowManager.ToggleNativeJournal();
    }

    // The vanilla log was closed: reopen the journal once the old window is gone.
    private void HandlePendingReturn(long now, bool addonVisible)
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
            if (Services.GameGui.GetAddonByName("FishingCluesJournalNative").IsNull)
            {
                normalLogReturnPending = false;
                lastReplacement = now;
                _ = ReturnFromNormalLogAsync();
            }
            else ReturnStatus = "Waiting for previous custom journal to finish closing.";
        }
    }

    private unsafe void HandleNormalLogClosed(long now, AgentFishingNote* agent, bool agentActive)
    {
        normalLogCloseRequested = false;
        normalLogWasVisible = false;
        allowExplicitVanillaLog = false;
        replacementRequested = false;
        if (configuration.ReplaceNormalFishingLog && Services.ClientState.IsLoggedIn)
        {
            if (agentActive) ((AgentInterface*)agent)->Hide();
            lastReplacement = now;
            ReturnStatus = "Close observed; waiting for window teardown.";
            normalLogReturnPending = true;
            normalLogReturnAfter = now + ReturnDelayMs;
            windowManager.CloseNativeJournal();
        }
        else ReturnStatus = $"Close observed; skipped (replacement={configuration.ReplaceNormalFishingLog}, loggedIn={Services.ClientState.IsLoggedIn}).";
    }

    // Hides the vanilla log and opens the journal. The journal's own open sound is
    // silenced because the vanilla log has just played one.
    private unsafe void ReplaceVanillaLog(long now, AgentFishingNote* agent, bool agentActive, AtkUnitBasePtr ptr, bool addonVisible)
    {
        lastReplacement = now;
        if (agentActive)
            ((AgentInterface*)agent)->Hide();
        if (addonVisible)
        {
            var addon = (AtkUnitBase*)ptr.Address;
            if (addon != null && addon->IsReady)
                addon->Close(true);
        }
        windowManager.ToggleNativeJournal(silenceOpenSound: true);
    }

    // PostSetup and PreDraw run inside the game's own addon lifecycle, so this
    // only flags the request. Hiding or closing the addon (or its agent) from
    // inside that callback corrupts AgentFishingNote's region and spot data,
    // leaving the vanilla log's lists empty until the game restarts. The hide and
    // close happen on the next framework tick (ReplaceVanillaLog).
    private void OnFishingNoteIntercept(AddonEvent eventType, AddonArgs args)
    {
        if (normalLogReturnPending) return;
        if (!configuration.ReplaceNormalFishingLog || allowExplicitVanillaLog)
            return;
        replacementRequested = true;
    }

    // Deferred to the next framework tick, outside the game's hide/finalize callback.
    private void OnNormalLogClosed(AddonEvent eventType, AddonArgs args)
    {
        if (allowExplicitVanillaLog || normalLogWasVisible)
        {
            normalLogCloseRequested = true;
            ReturnStatus = $"Received {eventType}; return queued.";
        }
    }

    private async Task ReturnFromNormalLogAsync()
    {
        await windowManager.OpenNativeJournalAsync();
        ReturnStatus = windowManager.IsNativeJournalOpen
            ? "Custom journal reopened."
            : "Open requested, but custom addon is not visible; check plugin log for setup errors.";
    }

    // The button in the journal that opens the real Fishing Log.
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
        replacementRequested = false;
        // Showing the agent alone can leave an empty shell if the command was intercepted before.
        if (fishingLogCommandId != 0)
        {
            UIModuleInterface* module = (UIModuleInterface*)UIModule.Instance();
            if (module != null) module->ExecuteMainCommand(fishingLogCommandId);
        }
        else ((AgentInterface*)agent)->Show();
    }
}
