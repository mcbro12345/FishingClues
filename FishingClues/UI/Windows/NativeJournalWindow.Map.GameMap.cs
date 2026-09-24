using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Lumina.Data.Files;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;
using KamiToolKit.Timelines;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// The round fish button beside "Locations Discovered". It opens the game's own map at the
// picked hole and marks it the way the Gathering Log marks a node: the fish icon and the
// hole's range circle.
public sealed partial class NativeJournalWindow
{
    private const float GameMapButtonSize = 28.0f;
    // Big enough for the fish to fill the circle (the icon has empty margins around the fish).
    private const float GameMapButtonIconSize = 30.0f;
    // The fish is not centred in its icon (measured: about 1.5px left and 1px low of the icon's
    // middle at 64px), so it is shifted by that much, scaled to the button.
    private static readonly Vector2 GameMapButtonIconOffset = new(0.8f, -0.5f);
    // The cog's circle in the 2x sheet, and how much of its middle holds the cog and its shadow.
    private const string CircleButtonSheetPath = "ui/uld/CircleButtons_hr1.tex";
    private const int CircleSpriteSize = 56;
    private const double CircleGlyphRadius = 17.5;
    private const string GameMapButtonTooltip = "Show this fishing hole on the main map";
    private const string GameMapButtonDisabledTooltip = "Select a fishing hole first";

    private TextureButtonNode? gameMapButton;
    private ImGuiImageNode? gameMapButtonIcon;
    // Shown only once the circle and the fish have loaded: an image node with no texture draws black.
    private bool gameMapCircleReady;
    private bool gameMapIconReady;

    private void CreateGameMapButton()
    {
        // The circle the settings cog sits on, with the cog painted out (see BuildBlankCircle).
        gameMapButton = new TextureButtonNode
        {
            Size = new Vector2(GameMapButtonSize, GameMapButtonSize),
            TextTooltip = GameMapButtonDisabledTooltip,
            IsEnabled = false,
            IsVisible = false,
            OnClick = OpenGameMap,
        };
        gameMapButton.AttachNode(this);
        _ = LoadGameMapCircleAsync(gameMapButton);
        float inset = (GameMapButtonSize - GameMapButtonIconSize) / 2.0f;
        gameMapButtonIcon = new ImGuiImageNode
        {
            Size = new Vector2(GameMapButtonIconSize, GameMapButtonIconSize),
            Position = new Vector2(inset, inset) + GameMapButtonIconOffset,
            FitTexture = true,
            Alpha = 0.0f,
        };
        gameMapButtonIcon.AttachNode(gameMapButton);
        AddGameMapIconTimeline(gameMapButtonIcon, gameMapButtonIcon.Position);
        _ = LoadGameMapButtonIconAsync(gameMapButtonIcon);
    }

    // The button animates only its own circle. This gives the fish the same press (down a
    // pixel) and disabled (dimmed) states, on the same frame numbers as the button's timeline
    // (see ButtonBase.LoadTwoPartTimelines), so it moves and dims along with the circle.
    private static void AddGameMapIconTimeline(ImGuiImageNode icon, Vector2 rest)
    {
        var full = new Vector3(100.0f);
        icon.AddTimeline(new TimelineBuilder()
            .AddFrameSetWithFrame(1, 9, 1, rest, 255, multiplyColor: full)
            .AddFrameSetWithFrame(10, 19, 10, rest, 255, multiplyColor: full)
            .AddFrameSetWithFrame(20, 29, 20, rest + new Vector2(0.0f, 1.0f), 255, multiplyColor: full)
            .AddFrameSetWithFrame(30, 39, 30, rest, 178, multiplyColor: new Vector3(50.0f))
            .AddFrameSetWithFrame(40, 49, 40, rest, 255, multiplyColor: full)
            .AddFrameSetWithFrame(50, 59, 50, rest, 255, multiplyColor: full)
            .Build());
    }

    // The circle sprites all have their glyph baked in, so the cog is painted out: inside the
    // area the glyph covers, each row is filled by blending between the clean shading just
    // outside it on either side.
    private static byte[]? BuildBlankCircle()
    {
        var sheet = Services.DataManager.GetFile<TexFile>(CircleButtonSheetPath);
        if (sheet is null) return null;
        int width = sheet.Header.Width;
        byte[] source = sheet.ImageData;
        const int size = CircleSpriteSize;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int from = (y * width + x) * 4, to = (y * size + x) * 4;
                // Lumina's image data is BGRA, the texture RGBA.
                rgba[to + 0] = source[from + 2];
                rgba[to + 1] = source[from + 1];
                rgba[to + 2] = source[from + 0];
                rgba[to + 3] = source[from + 3];
            }
        double centre = (size - 1) / 2.0;
        for (int y = 0; y < size; y++)
        {
            double dy = y - centre;
            if (Math.Abs(dy) >= CircleGlyphRadius) continue;
            double half = Math.Sqrt(CircleGlyphRadius * CircleGlyphRadius - dy * dy);
            int left = (int)Math.Floor(centre - half) - 1, right = (int)Math.Ceiling(centre + half) + 1;
            for (int x = left + 1; x < right; x++)
            {
                double t = (x - left) / (double)(right - left);
                for (int c = 0; c < 3; c++)
                {
                    int a = rgba[(y * size + left) * 4 + c], b = rgba[(y * size + right) * 4 + c];
                    rgba[(y * size + x) * 4 + c] = (byte)Math.Round(a + (b - a) * t);
                }
                rgba[(y * size + x) * 4 + 3] = 255;
            }
        }
        return rgba;
    }

    private async Task LoadGameMapCircleAsync(TextureButtonNode button)
    {
        try
        {
            byte[]? pixels = await Task.Run(BuildBlankCircle);
            if (pixels is null) return;
            var texture = await Services.TextureProvider.CreateFromRawAsync(
                RawImageSpecification.Rgba32(CircleSpriteSize, CircleSpriteSize), pixels, "FishingCluesGameMapCircle");
            await Services.Framework.Run(() =>
            {
                if (!ReferenceEquals(button, gameMapButton))
                {
                    texture.Dispose();
                    return;
                }
                if (button.ImageNode is not ImGuiImageNode image) { texture.Dispose(); return; }
                image.LoadTexture(texture);
                image.TextureSize = new Vector2(CircleSpriteSize, CircleSpriteSize);
                gameMapCircleReady = true;
            });
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "[FishingClues] Game map button: failed to build the button circle.");
        }
    }

    private async Task LoadGameMapButtonIconAsync(ImGuiImageNode node)
    {
        Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap texture;
        try
        {
            texture = await Services.TextureProvider.GetFromGameIcon(FishMapMarkerIconId).RentAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "[FishingClues] Game map button: failed to load the fish icon.");
            return;
        }
        await Services.Framework.Run(() =>
        {
            if (!ReferenceEquals(node, gameMapButtonIcon))
            {
                texture.Dispose();
                return;
            }
            node.LoadTexture(texture);
            node.Alpha = 1.0f;
            gameMapIconReady = true;
        });
    }

    // The button belongs to the caption, so it shows with it, and works once a hole in this
    // area is picked. Called every frame; only writes when something changed.
    private void UpdateGameMapButton()
    {
        if (gameMapButton is null) return;
        bool show = MapEnabled && gameMapCircleReady && gameMapIconReady;
        if (gameMapButton.IsVisible != show) gameMapButton.IsVisible = show;
        bool enabled = show && GameMapSpot() is not null;
        if (gameMapButton.IsEnabled != enabled)
        {
            gameMapButton.IsEnabled = enabled;
            gameMapButton.TextTooltip = enabled ? GameMapButtonTooltip : GameMapButtonDisabledTooltip;
        }
    }

    private void PositionGameMapButton(float areaRight, float captionTop)
    {
        // On the "Locations Discovered" line, at the right end of the caption.
        if (gameMapButton is not null)
            gameMapButton.Position = contentOrigin + new Vector2(areaRight - GameMapButtonSize - 6.0f, captionTop - 1.0f);
    }

    private JournalSpot? GameMapSpot()
        => selectedSpot is { } spot && mapArea is not null && spot.MapPixelPosition is not null
            && mapArea.Spots.Any(s => s.Id == spot.Id) ? spot : null;

    // The style flag GatherBuddy gives its temporary gathering markers.
    private const uint GatheringMarkerStyle = 4;

    // Puts the hole on the game's map as a temporary gathering marker (icon, range circle and
    // name) and opens the map in Gathering Log mode, the way GatherBuddy does it, but without
    // its red flag. The marker goes in before the map opens.
    private unsafe void OpenGameMap()
    {
        if (GameMapSpot() is not { WorldPosition: Vector2 raw } spot) return;
        if (!Services.DataManager.GetExcelSheet<TerritoryTypeSheet>().TryGetRow(spot.TerritoryId, out var territory)
            || territory.Map.ValueNullable is not { } map) return;
        var agent = AgentMap.Instance();
        if (agent is null) return;
        float scale = map.SizeFactor / 100.0f;
        agent->TempMapMarkerCount = 0;
        agent->AddGatheringTempMarker(GameMapCoordinate(raw.X, scale) - map.OffsetX, GameMapCoordinate(raw.Y, scale) - map.OffsetY,
            spot.Radius / 7, FishMapMarkerIconId, GatheringMarkerStyle, spot.Name);
        agent->OpenMap(map.RowId, spot.TerritoryId, spot.Name, MapType.GatheringLog);
    }

    // A fishing spot's X or Z as the map agent's marker coordinate: out to the map's whole-number
    // coordinate and back into the agent's internal units (GatherBuddy's MarkerToMap and
    // IntegerToInternal), before the map's own offset is taken off.
    private static int GameMapCoordinate(float raw, float scale)
    {
        int integral = (int)(2 * raw / scale + 100.9);
        return (int)(integral - 100 - 2048 / scale) / 2;
    }
}
