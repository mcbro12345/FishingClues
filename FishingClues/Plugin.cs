using System;
using System.Text;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FishingSpotSheet = Lumina.Excel.Sheets.FishingSpot;

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
    private readonly AvailabilityService availability;
    private readonly FishDetailsFormatter formatter;
    private readonly GuideDetailsService guideDetails;
    private readonly WindowManager windowManager;
    private readonly NormalLogReplacementController normalLog;
    private readonly ItemContextMenuService itemMenu;
    private bool journalKeybindWasDown;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();

        configuration = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.ApplySimplifiedJournalSettings();
        MigrateConfiguration(configuration);
        Services.PluginInterface.SavePluginConfig(configuration);

        fishData = new FishDataService();
        journal = new JournalBuilder(fishData, configuration);
        formatter = new FishDetailsFormatter(fishData, configuration);
        availability = new AvailabilityService(fishData, journal, formatter, configuration);
        guideDetails = new GuideDetailsService(fishData, journal, formatter);
        windowManager = new WindowManager(configuration, fishData, journal, availability, formatter, guideDetails, LogJournalDiagnostics);
        normalLog = new NormalLogReplacementController(configuration, windowManager, journal);
        windowManager.OpenNormalFishingLog = normalLog.OpenNormalFishingLog;
        itemMenu = new ItemContextMenuService(fishData, formatter,
            openGuide: (query, itemId) => windowManager.OpenGuideAsync(query, itemId),
            openClues: windowManager.OpenClues);

        Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the native Fishing Clues journal.\n/fishingclues settings → Open settings.",
        });
        Services.Framework.Update += OnFrameworkUpdate;
        Services.PluginInterface.UiBuilder.Draw += OnDraw;
        Services.PluginInterface.UiBuilder.OpenMainUi += windowManager.OpenJournal;
        Services.PluginInterface.UiBuilder.OpenConfigUi += windowManager.OpenSettings;
    }

    private static void MigrateConfiguration(Configuration configuration)
    {
        if (configuration.Version < 9)
        {
            configuration.NativeAreaWidth = 280.0f;
            configuration.Version = 9;
        }
        if (configuration.Version < 10)
        {
            configuration.Version = 10;
        }
        if (configuration.Version < 11)
        {
            configuration.NativeAreaWidth = 350.0f;
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
            configuration.Version = 13;
        }
        if (configuration.Version < 15)
        {
            configuration.NativeWindowWidth = 1050.0f;
            configuration.Version = 15;
        }
        if (configuration.Version < 16)
        {
            // the dropdown inset sliders used to read the actual pixel inset
            // directly; they now read relative to the preferred baseline below,
            // so an existing custom value needs to shift to stay at the same
            // effective inset instead of jumping when the baseline is applied
            if (configuration.NativeAreaDropdownLeftInset != 0.0f || configuration.NativeAreaDropdownRightInset != 0.0f)
            {
                configuration.NativeAreaDropdownLeftInset -= Configuration.DropdownLeftInsetBaseline;
                configuration.NativeAreaDropdownRightInset -= Configuration.DropdownRightInsetBaseline;
            }
            configuration.Version = 16;
        }
        if (configuration.Version < 17)
        {
            configuration.NativeRegionWidth = 181.0f;
            configuration.NativeAreaWidth = 286.0f;
            configuration.NativeAreaDropdownLeftInset = 1.0f;
            configuration.NativeAreaDropdownRightInset = 0.0f;
            configuration.Version = 17;
        }
        if (configuration.Version < 18)
        {
            // the left inset that just became the confirmed-clickable setup (slider
            // value 1 against the old baseline) is now the baseline itself, so the
            // slider reads 0 there instead of 1.
            configuration.NativeAreaDropdownLeftInset = 0.0f;
            configuration.Version = 18;
        }
        if (configuration.Version < 19)
        {
            // new confirmed default for the area/fish divider position
            configuration.NativeAreaWidth = 298.0f;
            configuration.Version = 19;
        }
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
        if (arguments.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase))
            windowManager.OpenSettings();
        else if (arguments.Trim().Equals("diagnostics", StringComparison.OrdinalIgnoreCase))
            LogJournalDiagnostics();
        else
            windowManager.OpenJournal();
    }

    private void OnDraw()
    {
        windowManager.Draw();
        itemMenu.DrawItemMenu();
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        fishData.CheckAutoRefresh(configuration);
        journal.ObserveVanillaRegionLabels();
        windowManager.SyncGuideWithJournal();
        HandleJournalKeybind();
        normalLog.OnFrameworkUpdate();
    }

    private void HandleJournalKeybind()
    {
        if (configuration.JournalKeybindEnabled && configuration.JournalKeybindKey != 0 && !configuration.ReplaceNormalFishingLog)
        {
            var key = (VirtualKey)configuration.JournalKeybindKey;
            bool down = Services.KeyState.IsVirtualKeyValid(key) && Services.KeyState[key]
                && (!configuration.JournalKeybindCtrl || Services.KeyState[VirtualKey.CONTROL])
                && (!configuration.JournalKeybindAlt || Services.KeyState[VirtualKey.MENU])
                && (!configuration.JournalKeybindShift || Services.KeyState[VirtualKey.SHIFT]);
            if (down && !journalKeybindWasDown && Services.ClientState.IsLoggedIn)
                windowManager.OpenJournal();
            journalKeybindWasDown = down;
        }
        else journalKeybindWasDown = false;
    }

    // Lists every image node (and the texture file behind it) in the game's own
    // world map window, to find which assets make up its base, frame and title
    // banner. Only produces output while that window ("AreaMap") is open.
    private static unsafe void DescribeNativeMapTextures(StringBuilder report)
    {
        try
        {
            var addonPtr = Services.GameGui.GetAddonByName("AreaMap");
            if (addonPtr.IsNull)
            {
                report.AppendLine("Native map textures: the world map window is not open (open it, then copy this report again).");
                return;
            }
            var addon = (AtkUnitBase*)addonPtr.Address;
            report.AppendLine($"Native map textures (AreaMap, scale={addon->Scale}):");
            DescribeNodeList(report, &addon->UldManager, 1);
        }
        catch (Exception ex)
        {
            report.AppendLine($"Native map textures: failed ({ex.Message})");
        }
    }

    private static unsafe void DescribeNodeList(StringBuilder report, AtkUldManager* manager, int depth)
    {
        if (manager == null || depth > 6) return;
        for (int i = 0; i < manager->NodeListCount; i++)
        {
            AtkResNode* node = manager->NodeList[i];
            if (node == null) continue;
            string indent = new(' ', depth * 2);
            if (node->Type == NodeType.Image)
            {
                var image = (AtkImageNode*)node;
                report.AppendLine($"{indent}Image id={node->NodeId} pos=({node->X:0.#},{node->Y:0.#}) size=({node->Width:0.#}x{node->Height:0.#}) visible={node->IsVisible()} tex={ImageTexturePath(image)}");
            }
            else if (node->Type == NodeType.Text)
                report.AppendLine($"{indent}Text id={node->NodeId} pos=({node->X:0.#},{node->Y:0.#}) size=({node->Width:0.#}x{node->Height:0.#}) visible={node->IsVisible()} text={((AtkTextNode*)node)->NodeText}");
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                report.AppendLine($"{indent}Component id={node->NodeId} type={node->Type} visible={node->IsVisible()}");
                if (component != null) DescribeNodeList(report, &component->UldManager, depth + 1);
            }
        }
    }

    private static unsafe string ImageTexturePath(AtkImageNode* image)
    {
        try
        {
            if (image->PartsList == null || image->PartId >= image->PartsList->PartCount) return "(no part)";
            var part = &image->PartsList->Parts[image->PartId];
            var asset = part->UldAsset;
            if (asset == null) return "(no asset)";
            string rect = $" part={image->PartId} rect=({part->U},{part->V},{part->Width}x{part->Height})";
            var resource = asset->AtkTexture.Resource;
            if (resource == null || resource->TexFileResourceHandle == null) return "(no resource)";
            return resource->TexFileResourceHandle->FileName.ToString() + rect;
        }
        catch (Exception ex)
        {
            return $"(error {ex.Message})";
        }
    }

    private unsafe void LogJournalDiagnostics()
    {
        var report = new StringBuilder();
        report.AppendLine($"Fishing Clues {typeof(Plugin).Assembly.GetName().Version}");
        report.AppendLine($"Client structs: {typeof(PlayerState).Assembly.GetName().Version}");
        report.AppendLine($"Normal-log return: {normalLog.ReturnStatus}");
        report.AppendLine($"Layout: regionWidth={configuration.NativeRegionWidth:0.#}; areaWidth={configuration.NativeAreaWidth:0.#}");
        report.AppendLine($"Replacement={configuration.ReplaceNormalFishingLog}; loggedIn={Services.ClientState.IsLoggedIn}; explicit={normalLog.AllowExplicitVanillaLog}; seenVisible={normalLog.WasNormalLogVisible}; closeQueued={normalLog.IsCloseQueued}; customOpen={windowManager.IsNativeJournalOpen}");
        report.AppendLine(windowManager.DescribeMapState().TrimEnd());
        DescribeNativeMapTextures(report);
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
            foreach (FishingSpotSheet spot in Services.DataManager.GetExcelSheet<FishingSpotSheet>())
            {
                if (spot.PlaceName.RowId == 0) continue;
                // IDs and flags only, never fish/spot names not yet unlocked
                string discovered = player == null ? "unavailable"
                    : spot.RowId >= player->UnlockedFishingSpotsBitArray.BitCount ? "out-of-range"
                    : JournalBuilder.IsFishingHoleDiscovered(player, spot.RowId).ToString();
                report.AppendLine($"Hole row={spot.RowId}, order={spot.Order}, main={spot.PlaceNameMain.RowId}, sub={spot.PlaceNameSub.RowId}, place={spot.PlaceName.RowId}, discovered={discovered}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Report failed: {ex.GetType().Name}: {ex.Message}");
            Services.Log.Error(ex, "Could not capture all fishing diagnostics.");
        }
        windowManager.ShowDiagnostics(report.ToString());
    }
}
