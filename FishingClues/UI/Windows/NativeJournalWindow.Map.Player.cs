using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using KamiToolKit.Nodes;

using FishingClues.Base;

using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace FishingClues.UI.Windows;

// player marker: blue drop icon plus camera cone, placed by hand on the unscaled layer
public sealed partial class NativeJournalWindow
{
    private const uint MapPlayerIconId = 60443;
    private const float MapPlayerIconSize = 32.0f;
    // the drop's centre hole, what the marker rotates around
    private static readonly Vector2 MapPlayerIconPivot = new(16.5f, 15.5f);

    // camera cone: 96x96 sprite at (352,0) of NaviMap.tex, cut from the 2x sheet. Origin lines its back up with the drop
    private const string MapConeSheetPath = "ui/uld/NaviMap_hr1.tex";
    private const int MapConeSheetX = 704;
    private const int MapConeSheetSize = 192;
    private const int MapPlayerConeSize = 96;
    private static readonly Vector2 MapPlayerConeOrigin = new(22.5f, 71.5f);
    private const float MapPlayerConeBaseAngle = MathF.PI / 4.0f;
    // Flip to -1 if the marker and cone turn the wrong way.
    private const float MapRotationSign = 1.0f;

    private ImGuiImageNode? playerMarker;
    private ImGuiImageNode? playerCone;
    private uint playerMapTerritory;
    private (float Scale, float OffsetX, float OffsetY)? playerMapInfo;

    private void CreatePlayerMarker()
    {
        if (playerLayer is null) return;
        // Cone first so the drop draws on top of it.
        playerCone = new ImGuiImageNode
        {
            Size = new Vector2(MapPlayerConeSize, MapPlayerConeSize),
            FitTexture = true,
            Origin = MapPlayerConeOrigin,
            Alpha = 0.0f,
            IsVisible = false,
        };
        playerCone.AttachNode(playerLayer);
        _ = LoadPlayerConeAsync(playerCone);
        var marker = playerMarker = new ImGuiImageNode
        {
            Size = new Vector2(MapPlayerIconSize, MapPlayerIconSize),
            FitTexture = true,
            Origin = MapPlayerIconPivot,
            Alpha = 0.0f,
            IsVisible = false,
        };
        playerMarker.AttachNode(playerLayer);
        _ = LoadGameIconAsync(playerMarker, MapPlayerIconId, () => ReferenceEquals(marker, playerMarker));
    }

    private async Task LoadPlayerConeAsync(ImGuiImageNode node)
    {
        byte[]? pixels = await Task.Run(() => ReadRgbaSquare(MapConeSheetPath, MapConeSheetX, 0, MapConeSheetSize));
        if (pixels is not null)
            await ShowRgbaAsync(node, pixels, MapConeSheetSize, "FishingCluesMapPlayerCone", () => ReferenceEquals(node, playerCone));
    }
    private unsafe void UpdatePlayerMarker()
    {
        if (playerMarker is null || playerCone is null || mapClip is null) return;
        var player = Services.ObjectTable.LocalPlayer;
        uint territory = Services.ClientState.TerritoryType;
        if (player is null || mapArea is null || mapArea.Spots.Count == 0 || mapArea.Spots[0].TerritoryId != territory
            || ResolvePlayerMapInfo(territory) is not var (mapScale, offsetX, offsetY))
        {
            playerMarker.IsVisible = playerCone.IsVisible = false;
            return;
        }
        Vector2 pixel = new((player.Position.X + offsetX) * mapScale + 1024.0f, (player.Position.Z + offsetY) * mapScale + 1024.0f);
        Vector2 local = (pixel - new Vector2(mapPanX, mapPanY)) * MapScale();
        bool visible = local.X >= -MapPlayerConeSize && local.Y >= -MapPlayerConeSize
            && local.X <= mapClip.Width + MapPlayerConeSize && local.Y <= mapClip.Height + MapPlayerConeSize;
        playerMarker.IsVisible = playerCone.IsVisible = visible;
        if (!visible) return;

        // The game's heading 0 faces +Z (south); the icon is drawn pointing north.
        playerMarker.Position = local - MapPlayerIconPivot;
        playerMarker.Rotation = MapRotationSign * (MathF.PI - player.Rotation);
        playerCone.Position = local - MapPlayerConeOrigin;
        var camera = CameraManager.Instance();
        var active = camera is null ? null : camera->GetActiveCamera();
        playerCone.IsVisible = active is not null;
        if (active is not null) playerCone.Rotation = MapRotationSign * -active->DirH - MapPlayerConeBaseAngle;
    }

    // map scale/offsets for world -> pixel: (world + offset) * scale + 1024, cached per zone
    private (float Scale, float OffsetX, float OffsetY)? ResolvePlayerMapInfo(uint territory)
    {
        if (playerMapTerritory != territory)
        {
            playerMapTerritory = territory;
            playerMapInfo = null;
            if (Services.DataManager.GetExcelSheet<TerritoryTypeSheet>().TryGetRow(territory, out var row)
                && row.Map.ValueNullable is { } map && map.SizeFactor != 0)
                playerMapInfo = (map.SizeFactor / 100.0f, map.OffsetX, map.OffsetY);
        }
        return playerMapInfo;
    }
}
