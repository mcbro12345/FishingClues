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
    private const string LocateTooltip = "Select the nearest fishing hole";
    private const string LocateDisabledTooltip = "Select the nearest fishing hole (no unlocked fishing holes in this area)";
    private const long LocateCheckIntervalMs = 400;

    private TextureButtonNode? locateButton;
    private TextureButtonNode? settingsButton;

    // The map toggle and this locate button sit as a pair in the region column's
    // bottom right corner. Their 28px sprites have about 5px of clear padding
    // around a ~17px gold button, so the boxes overlap by 8px and the pair
    // hangs 3px past the column's edge.
    private const float CornerButtonSize = 28.0f;
    private const float CornerButtonGap = -8.0f;
    private const float CornerButtonRightOverhang = 3.0f;

    private Vector2 MapButtonPosition()
    {
        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        return new Vector2(contentOrigin.X + regionWidth + CornerButtonRightOverhang - CornerButtonSize, FooterPosition.Y);
    }

    private const float SettingsButtonSize = 28.0f;

    // Title bar, laid out as in the game's own command panel (its diagnostics
    // report: close button 28x28 at x=258,y=10; round settings button 28x28 at
    // x=232,y=6): the round button sits 26px left of the close button's left edge
    // and 4px above its top. Our close button is at (Width - 33, 6).
    private void PositionSettingsButton()
    {
        if (settingsButton is null) return;
        settingsButton.Position = new Vector2(Size.X - 33.0f - 26.0f, 6.0f - 1.0f);
    }

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
        PositionSettingsButton();
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

    // The nearest unlocked fishing hole in the player's current zone; null only
    // when the zone has no unlocked holes at all. Previously this required the
    // player to be standing inside the hole's range circle, but that circle
    // (derived from the sheet's casting Radius) is often smaller than where the
    // hole actually lets you fish, which left the button greyed out while
    // standing right at an unlocked hole.
    private JournalSpot? FindCurrentHole()
    {
        uint territory = Services.ClientState.TerritoryType;
        if (territory == 0) return null;

        var candidates = regions.SelectMany(r => r.Areas).SelectMany(a => a.Spots)
            .Where(s => s.TerritoryId == territory && s.IsUnlocked).ToList();
        if (candidates.Count <= 1) return candidates.Count == 0 ? null : candidates[0];

        var player = Services.ObjectTable.LocalPlayer;
        if (player is null || ResolvePlayerMapInfo(territory) is not var (scale, offsetX, offsetY))
            return candidates[0];

        Vector2 playerPixel = new((player.Position.X + offsetX) * scale + 1024.0f, (player.Position.Z + offsetY) * scale + 1024.0f);
        return candidates.MinBy(spot => spot.MapPixelPosition is Vector2 pixel ? Vector2.Distance(playerPixel, pixel) : float.MaxValue);
    }
}
