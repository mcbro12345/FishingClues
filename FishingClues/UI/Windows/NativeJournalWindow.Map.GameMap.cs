using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Lumina.Data.Files;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using KamiToolKit.Nodes;
using KamiToolKit.Timelines;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// fish button by "Locations Discovered", opens the main map at the picked hole
public sealed partial class NativeJournalWindow
{
    private const float GameMapButtonSize = 28.0f;
    private const float GameMapButtonIconSize = 30.0f;
    // the fish isn't centred in its icon, this is the measured offset
    private static readonly Vector2 GameMapButtonIconOffset = new(0.8f, -0.5f);
    // the cog's circle sprite and the radius of the glyph to paint out
    private const string CircleButtonSheetPath = "ui/uld/CircleButtons_hr1.tex";
    private const int CircleSpriteSize = 56;
    private const double CircleGlyphRadius = 17.5;
    private const string GameMapButtonTooltip = "Show this fishing hole on the main map";
    private const string GameMapButtonDisabledTooltip = "Select a fishing hole first";

    private TextureButtonNode? gameMapButton;
    private ImGuiImageNode? gameMapButtonIcon;
    // hidden until both textures load, no texture draws black
    private bool gameMapCircleReady;
    private bool gameMapIconReady;

    private void CreateGameMapButton()
    {
        // cog circle with the cog painted out
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
        var icon = gameMapButtonIcon = new ImGuiImageNode
        {
            Size = new Vector2(GameMapButtonIconSize, GameMapButtonIconSize),
            Position = new Vector2(inset, inset) + GameMapButtonIconOffset,
            FitTexture = true,
            Alpha = 0.0f,
        };
        gameMapButtonIcon.AttachNode(gameMapButton);
        AddGameMapIconTimeline(gameMapButtonIcon, gameMapButtonIcon.Position);
        _ = LoadGameIconAsync(icon, FishMapMarkerIconId, () => ReferenceEquals(icon, gameMapButtonIcon), () => gameMapIconReady = true);
    }

    // same press/disabled frames as the button's own timeline so the fish follows it
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

    // there's no blank circle sprite, so blend the shading across the cog
    private static byte[]? BuildBlankCircle()
    {
        var rgba = ReadRgbaSquare(CircleButtonSheetPath, 0, 0, CircleSpriteSize);
        if (rgba is null) return null;
        const int size = CircleSpriteSize;
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
        byte[]? pixels = await Task.Run(BuildBlankCircle);
        if (pixels is not null && button.ImageNode is ImGuiImageNode image)
            await ShowRgbaAsync(image, pixels, CircleSpriteSize, "FishingCluesGameMapCircle",
                () => ReferenceEquals(button, gameMapButton), () => gameMapCircleReady = true);
    }
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
        if (gameMapButton is not null)
            gameMapButton.Position = contentOrigin + new Vector2(areaRight - GameMapButtonSize - 6.0f, captionTop - 1.0f);
    }

    private JournalSpot? GameMapSpot()
        => selectedSpot is { } spot && mapArea is not null && spot.MapPixelPosition is not null
            && mapArea.Spots.Any(s => s.Id == spot.Id) ? spot : null;

    private const uint GatheringMarkerStyle = 4;

    // same approach as GatherBuddy minus the flag, marker goes in before the map opens
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

    // GatherBuddy's MarkerToMap + IntegerToInternal
    private static int GameMapCoordinate(float raw, float scale)
    {
        int integral = (int)(2 * raw / scale + 100.9);
        return (int)(integral - 100 - 2048 / scale) / 2;
    }
}
