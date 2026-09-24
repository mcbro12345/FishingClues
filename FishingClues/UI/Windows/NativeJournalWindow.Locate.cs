using System;
using System.Linq;
using System.Numerics;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// button that selects the nearest hole
public sealed partial class NativeJournalWindow
{
    private const string LocateTooltip = "Select the nearest fishing hole";
    private const string LocateDisabledTooltip = "Select the nearest fishing hole (no unlocked fishing holes in this area)";
    private const long LocateCheckIntervalMs = 400;

    private TextureButtonNode? locateButton;
    private TextureButtonNode? settingsButton;

    // map toggle and this button sit as a pair, the sprites are padded so the boxes overlap
    private const float CornerButtonSize = 28.0f;
    private const float CornerButtonGap = -8.0f;
    private const float CornerButtonRightOverhang = 3.0f;

    private Vector2 MapButtonPosition()
    {
        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        return new Vector2(contentOrigin.X + regionWidth + CornerButtonRightOverhang - CornerButtonSize, FooterPosition.Y);
    }

    private const float SettingsButtonSize = 28.0f;

    // same layout as the game's own title bar
    private void PositionSettingsButton()
    {
        if (settingsButton is null) return;
        settingsButton.Position = new Vector2(Size.X - 33.0f - 26.0f, 6.0f - 1.0f);
    }

    private JournalSpot? currentHole;
    private long nextLocateCheck;

    private void CreateLocateButton()
    {
        locateButton = new TextureButtonNode
        {
            Size = new Vector2(CornerButtonSize, CornerButtonSize),
            TexturePath = "ui/uld/AreaMap.tex",
            TextureCoordinates = new Vector2(144.0f, 0.0f),
            TextureSize = new Vector2(28.0f, 28.0f),
            TextTooltip = LocateDisabledTooltip,
            IsEnabled = false,
            OnClick = () => { if (currentHole is { } hole) NavigateToSpot(hole, zoomToSpot: true); },
        };
        locateButton.AttachNode(this);
    }

    private void PositionLocateButton()
    {
        PositionSettingsButton();
        if (locateButton is null) return;
        locateButton.Position = MapButtonPosition() - new Vector2(CornerButtonSize + CornerButtonGap, 0.0f);
        locateButton.IsVisible = !GuideMode;
    }

    private void UpdateLocateButton()
    {
        if (locateButton is null || GuideMode) return;
        long now = Environment.TickCount64;
        if (now < nextLocateCheck) return;
        nextLocateCheck = now + LocateCheckIntervalMs;

        currentHole = FindCurrentHole();
        bool enabled = currentHole is not null;
        if (locateButton.IsEnabled != enabled)
        {
            locateButton.IsEnabled = enabled;
            locateButton.TextTooltip = enabled ? LocateTooltip : LocateDisabledTooltip;
        }
    }

    // nearest unlocked hole in the current zone, null if there are none
    private JournalSpot? FindCurrentHole()
    {
        uint territory = Services.ClientState.TerritoryType;
        if (territory == 0) return null;

        var candidates = regions.AllSpots()
            .Where(s => s.TerritoryId == territory && s.IsUnlocked).ToList();
        if (candidates.Count <= 1) return candidates.Count == 0 ? null : candidates[0];

        var player = Services.ObjectTable.LocalPlayer;
        if (player is null || ResolvePlayerMapInfo(territory) is not var (scale, offsetX, offsetY))
            return candidates[0];

        Vector2 playerPixel = new((player.Position.X + offsetX) * scale + 1024.0f, (player.Position.Z + offsetY) * scale + 1024.0f);
        return candidates.MinBy(spot => spot.MapPixelPosition is Vector2 pixel ? Vector2.Distance(playerPixel, pixel) : float.MaxValue);
    }
}
