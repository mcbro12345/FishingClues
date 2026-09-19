using System;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Logic;
using FishingClues.UI;

namespace FishingClues;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
    private const string CommandName = "/fishingclues";

    private readonly Configuration configuration;
    private readonly FishDataService fishData;
    private readonly JournalBuilder journal;
    private readonly WindowManager windowManager;
    private readonly NormalLogReplacementController normalLog;
    private readonly ItemContextMenuService itemMenu;
    private bool journalKeybindWasDown;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();

        configuration = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Migrate();
        Services.PluginInterface.SavePluginConfig(configuration);

        fishData = new FishDataService();
        journal = new JournalBuilder(fishData, configuration);
        var formatter = new FishDetailsFormatter(fishData, configuration);
        var availability = new AvailabilityService(fishData, journal, formatter, configuration);
        var guideDetails = new GuideDetailsService(fishData, journal, formatter);
        windowManager = new WindowManager(configuration, fishData, journal, availability, formatter, guideDetails, ShowDiagnostics);
        normalLog = new NormalLogReplacementController(configuration, windowManager, journal);
        windowManager.OpenNormalFishingLog = normalLog.OpenNormalFishingLog;
        itemMenu = new ItemContextMenuService(fishData, formatter,
            openGuide: (query, itemId) => windowManager.OpenGuideAsync(query, itemId),
            openClues: windowManager.OpenClues);

        Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Fishing Clues journal.\n/fishingclues settings → Open settings.",
        });
        Services.Framework.Update += OnFrameworkUpdate;
        Services.PluginInterface.UiBuilder.Draw += OnDraw;
        Services.PluginInterface.UiBuilder.OpenMainUi += windowManager.OpenJournal;
        Services.PluginInterface.UiBuilder.OpenConfigUi += windowManager.OpenSettings;
    }

    public void Dispose()
    {
        bool wasReplacingLog = configuration.ReplaceNormalFishingLog && windowManager.IsNativeJournalOpen;
        itemMenu.Dispose();
        fishData.Dispose();
        normalLog.Dispose();
        Services.CommandManager.RemoveHandler(CommandName);
        Services.Framework.Update -= OnFrameworkUpdate;
        Services.PluginInterface.UiBuilder.Draw -= OnDraw;
        Services.PluginInterface.UiBuilder.OpenMainUi -= windowManager.OpenJournal;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= windowManager.OpenSettings;
        windowManager.Dispose();
        if (wasReplacingLog) normalLog.RestoreVanillaLog();
    }

    private void OnCommand(string command, string arguments)
    {
        string argument = arguments.Trim();
        if (argument.Equals("settings", StringComparison.OrdinalIgnoreCase))
            windowManager.OpenSettings();
        else if (argument.Equals("diagnostics", StringComparison.OrdinalIgnoreCase))
            ShowDiagnostics();
        else
            windowManager.OpenJournal();
    }

    private void ShowDiagnostics()
        => windowManager.ShowDiagnostics(new DiagnosticsReport(configuration, normalLog, windowManager).Build());

    private void OnDraw()
    {
        windowManager.Draw();
        itemMenu.DrawItemMenu();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        fishData.CheckAutoRefresh(configuration);
        journal.ObserveVanillaRegionLabels();
        windowManager.SyncGuideWithJournal();
        HandleJournalKeybind();
        normalLog.OnFrameworkUpdate();
    }

    private void HandleJournalKeybind()
    {
        if (!configuration.JournalKeybindEnabled || configuration.JournalKeybindKey == 0 || configuration.ReplaceNormalFishingLog)
        {
            journalKeybindWasDown = false;
            return;
        }
        var key = (VirtualKey)configuration.JournalKeybindKey;
        bool down = Services.KeyState.IsVirtualKeyValid(key) && Services.KeyState[key]
            && (!configuration.JournalKeybindCtrl || Services.KeyState[VirtualKey.CONTROL])
            && (!configuration.JournalKeybindAlt || Services.KeyState[VirtualKey.MENU])
            && (!configuration.JournalKeybindShift || Services.KeyState[VirtualKey.SHIFT]);
        if (down && !journalKeybindWasDown && Services.ClientState.IsLoggedIn)
            windowManager.OpenJournal();
        journalKeybindWasDown = down;
    }
}
