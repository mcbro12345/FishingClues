using System;
using System.Linq;
using System.Numerics;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// The "select the fishing hole I'm standing at" button, in the region column's
// bottom right corner just left of the map button. It works whether or not
// the area map is shown.
public sealed partial class NativeJournalWindow
{
    private const string LocateTooltip = "Select the fishing hole you are currently at";
    private const string LocateDisabledTooltip = "Select the fishing hole you are currently at (you are not at an unlocked fishing hole)";
    private const long LocateCheckIntervalMs = 400;

    private TextureButtonNode? locateButton;
    private JournalSpot? currentHole;
    private long nextLocateCheck;

    private void CreateLocateButton()
    {
        // The button from the game's own map window toolbar (the one under
        // the up arrow), framed like the other buttons.
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
        if (locateButton is null) return;
        // Just left of the map button in the region column's bottom right
        // corner (see MapButtonPosition).
        locateButton.Position = MapButtonPosition() - new Vector2(CornerButtonSize + CornerButtonGap, 0.0f);
        locateButton.IsVisible = !GuideMode;
    }

    // Called every frame; only does real work a couple of times a second.
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

    // The unlocked fishing hole whose range circle (the same one drawn on the
    // map) the player is standing inside, nearest one if several overlap; null
    // when the player is in no unlocked hole's range.
    private JournalSpot? FindCurrentHole()
    {
        uint territory = Services.ClientState.TerritoryType;
        var player = Services.ObjectTable.LocalPlayer;
        if (territory == 0 || player is null || ResolvePlayerMapInfo(territory) is not var (scale, offsetX, offsetY))
            return null;
        Vector2 playerPixel = new((player.Position.X + offsetX) * scale + 1024.0f, (player.Position.Z + offsetY) * scale + 1024.0f);

        JournalSpot? best = null;
        float bestDistance = float.MaxValue;
        foreach (JournalSpot spot in regions.SelectMany(r => r.Areas).SelectMany(a => a.Spots))
        {
            if (spot.TerritoryId != territory || !spot.IsUnlocked || spot.MapPixelPosition is not Vector2 pixel) continue;
            float distance = Vector2.Distance(playerPixel, pixel);
            if (distance > MarkerCircleRawDiameter(spot) / 2.0f || distance >= bestDistance) continue;
            best = spot;
            bestDistance = distance;
        }
        return best;
    }
}
