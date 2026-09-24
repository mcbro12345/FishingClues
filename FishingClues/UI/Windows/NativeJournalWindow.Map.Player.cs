using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using KamiToolKit.Nodes;
using Lumina.Data.Files;

using FishingClues.Base;

using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace FishingClues.UI.Windows;

// The player's own marker on the map: the game's blue drop icon and a light
// cone showing where the camera faces. Both sit on the unscaled mapClip layer
// and are placed by hand so they keep their size at any zoom.
public sealed partial class NativeJournalWindow
{
    private const uint MapPlayerIconId = 60443;
    private const float MapPlayerIconSize = 32.0f;
    // The centre hole of the drop inside its 32x32 icon, which sits on the
    // player's position and is what the marker rotates around.
    private static readonly Vector2 MapPlayerIconPivot = new(16.5f, 15.5f);

    // The camera cone is the 96x96 sprite at (352,0) of NaviMap.tex, read from
    // the 2x sheet NaviMap_hr1.tex (the 192x192 block at (704,0)). It fans out
    // toward the upper right from a bright corner. Its origin is the point
    // that lines the cone's rounded back up with the drop's.
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
        playerMarker = new ImGuiImageNode
        {
            Size = new Vector2(MapPlayerIconSize, MapPlayerIconSize),
            FitTexture = true,
            Origin = MapPlayerIconPivot,
            Alpha = 0.0f,
            IsVisible = false,
        };
        playerMarker.AttachNode(playerLayer);
        _ = LoadPlayerMarkerIconAsync(playerMarker);
    }

    private async Task LoadPlayerMarkerIconAsync(ImGuiImageNode node)
    {
        IDalamudTextureWrap texture;
        try
        {
            texture = await Services.TextureProvider.GetFromGameIcon(MapPlayerIconId).RentAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "[FishingClues] Area map: failed to load the player marker icon.");
            return;
        }
        await Services.Framework.Run(() =>
        {
            if (!ReferenceEquals(node, playerMarker))
            {
                texture.Dispose();
                return;
            }
            node.LoadTexture(texture);
            node.Alpha = 1.0f;
        });
    }

    private async Task LoadPlayerConeAsync(ImGuiImageNode node)
    {
        IDalamudTextureWrap texture;
        try
        {
            var pixels = await Task.Run(() =>
            {
                var sheet = Services.DataManager.GetFile<TexFile>(MapConeSheetPath);
                if (sheet is null) return null;
                int width = sheet.Header.Width;
                byte[] source = sheet.ImageData;
                var crop = new byte[MapConeSheetSize * MapConeSheetSize * 4];
                for (int y = 0; y < MapConeSheetSize; y++)
                {
                    for (int x = 0; x < MapConeSheetSize; x++)
                    {
                        int from = (y * width + MapConeSheetX + x) * 4;
                        int to = (y * MapConeSheetSize + x) * 4;
                        // Lumina's image data is BGRA, the texture RGBA.
                        crop[to + 0] = source[from + 2];
                        crop[to + 1] = source[from + 1];
                        crop[to + 2] = source[from + 0];
                        crop[to + 3] = source[from + 3];
                    }
                }
                return crop;
            });
            if (pixels is null) return;
            texture = await Services.TextureProvider.CreateFromRawAsync(
                RawImageSpecification.Rgba32(MapConeSheetSize, MapConeSheetSize), pixels, "FishingCluesMapPlayerCone");
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "[FishingClues] Area map: failed to load the camera cone sprite.");
            return;
        }
        await Services.Framework.Run(() =>
        {
            if (!ReferenceEquals(node, playerCone))
            {
                texture.Dispose();
                return;
            }
            node.LoadTexture(texture);
            node.TextureSize = new Vector2(MapConeSheetSize, MapConeSheetSize);
            node.Alpha = 1.0f;
        });
    }

    // Shown only while the player is in the zone whose map is displayed.
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

    // The zone map's scale and offsets, for converting a world position to map
    // pixels: (world + offset) * scale + 1024. Cached for the last zone asked
    // about; null when the zone has no usable map.
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
