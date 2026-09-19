using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FishingSpotSheet = Lumina.Excel.Sheets.FishingSpot;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Logic;
using FishingClues.UI;

namespace FishingClues;

// The text report behind the settings window's diagnostics button: plugin
// state plus what the game reports about fishing hole discovery.
internal sealed class DiagnosticsReport(Configuration configuration, NormalLogReplacementController normalLog, WindowManager windowManager)
{
    public unsafe string Build()
    {
        var report = new StringBuilder();
        report.AppendLine($"Fishing Clues {typeof(Plugin).Assembly.GetName().Version}");
        report.AppendLine($"Client structs: {typeof(PlayerState).Assembly.GetName().Version}");
        report.AppendLine($"Normal-log return: {normalLog.ReturnStatus}");
        report.AppendLine($"Layout: regionWidth={configuration.NativeRegionWidth:0.#}; areaWidth={configuration.NativeAreaWidth:0.#}");
        report.AppendLine($"Replacement={configuration.ReplaceNormalFishingLog}; loggedIn={Services.ClientState.IsLoggedIn}; explicit={normalLog.AllowExplicitVanillaLog}; seenVisible={normalLog.WasNormalLogVisible}; closeQueued={normalLog.IsCloseQueued}; customOpen={windowManager.IsNativeJournalOpen}");
        report.AppendLine(windowManager.DescribeMapState().TrimEnd());
        string layouts = windowManager.DescribeDetailsLayouts();
        if (layouts.Length > 0) report.AppendLine(layouts);
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
                // IDs and flags only, never fish or hole names that are not unlocked yet.
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
        return report.ToString();
    }
}
