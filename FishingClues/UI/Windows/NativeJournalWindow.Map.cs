using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// The optional area map shown under the area list: a scaled-down copy of the
// zone's own map texture with a marker per fishing hole in the currently
// viewed area, pannable by dragging and clickable to jump straight to a hole.
public sealed partial class NativeJournalWindow
{
    // Vanilla's own fishing-log icon atlas, already used for the journal/guide
    // footer buttons below - this coordinate is a best guess at its map-toggle
    // icon and is the one thing here most likely to need a pixel nudge once
    // it's actually visible in game.
    private static readonly Vector2 MapToggleTextureCoordinates = new(116.0f, 0.0f);
    private static readonly Vector2 MapToggleTextureSize = new(28.0f, 28.0f);
    private const float MapCaptionHeight = 40.0f;
    private const float MapImageHeight = 150.0f;
    private const float MapPanelSpacing = 8.0f;
    private const float MapToggleSize = 24.0f;
    private const float MapMarkerSize = 20.0f;
    // Vanilla's own map-flag icon - a real, confirmed-valid icon id, used here
    // as a stand-in until swapped for the exact fishing-hole marker icon.
    private const uint MapMarkerIconId = 60561;

    private ResNode? mapClip;
    private ResNode? mapContent;
    private SimpleImageNode? mapImage;
    private string? mapLoadedTexturePath;
    private LabelTextNode? mapAreaName;
    private LabelTextNode? mapDiscoveredLabel;
    private ButtonBase? mapToggleButton;
    private JournalArea? mapArea;
    private readonly Dictionary<IconImageNode, JournalSpot> mapMarkers = new();
    private float mapPanY;
    private bool draggingMap;
    private float mapDragStartMouseY;
    private float mapDragStartPanY;
    private float mapDragDistance;

    private bool MapEnabled => !GuideMode && configuration.ShowAreaLocationMap;

    private float ReservedMapHeight() => MapEnabled ? MapCaptionHeight + MapImageHeight + MapPanelSpacing : 0.0f;

    private float MapScale() => Math.Max(0.02f, areaWidthSetting / 2048.0f);

    private void BuildMapPanel()
    {
        mapAreaName = new LabelTextNode { Height = 18.0f, FontSize = 14, String = "" };
        mapAreaName.AttachNode(this);
        mapDiscoveredLabel = new LabelTextNode { Height = 16.0f, FontSize = 12, String = "" };
        mapDiscoveredLabel.AttachNode(this);

        mapClip = new ResNode { NodeFlags = NodeFlags.Clip | NodeFlags.Visible };
        mapClip.AttachNode(this);
        mapContent = new ResNode();
        mapContent.AttachNode(mapClip);
        mapImage = new SimpleImageNode { Size = new Vector2(2048.0f, 2048.0f) };
        mapImage.AttachNode(mapContent);

        try
        {
            mapToggleButton = new TextureButtonNode
            {
                TexturePath = "ui/uld/FishingNoteBook.tex",
                TextureCoordinates = MapToggleTextureCoordinates,
                TextureSize = MapToggleTextureSize,
                Size = new Vector2(MapToggleSize, MapToggleSize),
                TextTooltip = "Show/hide the area map",
                OnClick = ToggleMapVisibility,
            };
        }
        catch (Exception ex)
        {
            reportSetupError(ex);
            mapToggleButton?.Dispose();
            mapToggleButton = new TextButtonNode { String = "Map", Size = new Vector2(48.0f, MapToggleSize), OnClick = ToggleMapVisibility };
        }
        mapToggleButton.AttachNode(this);
    }

    private void ToggleMapVisibility()
    {
        configuration.ShowAreaLocationMap = !configuration.ShowAreaLocationMap;
        saveDivider();
        LayoutAttachedNodes();
    }

    // Called whenever an area gets expanded in the area list, so the map
    // always shows the fishing holes for whatever area the player just opened.
    private void ShowAreaMap(JournalArea area)
    {
        mapArea = area;
        if (mapAreaName is not null) mapAreaName.String = area.Name;
        int discovered = area.Spots.Count(s => s.IsUnlocked);
        if (mapDiscoveredLabel is not null)
            mapDiscoveredLabel.String = $"Locations Discovered: {discovered}/{area.Spots.Count}";
        if (!MapEnabled) return;

        var withMap = area.Spots.Where(s => s.MapPixelPosition is not null).ToArray();
        EnsureMapTexture(withMap.FirstOrDefault()?.MapTexturePath);
        RebuildMapMarkers(withMap);
        JournalSpot? focus = withMap.FirstOrDefault(s => s.IsUnlocked) ?? withMap.FirstOrDefault();
        CenterMapOn(focus?.MapPixelPosition ?? new Vector2(1024.0f, 1024.0f));
        ApplyMapPan();
    }

    private void EnsureMapTexture(string? texturePath)
    {
        if (mapImage is null || string.IsNullOrEmpty(texturePath) || texturePath == mapLoadedTexturePath)
            return;
        mapLoadedTexturePath = texturePath;
        mapImage.TexturePath = texturePath;
        mapImage.TextureSize = new Vector2(2048.0f, 2048.0f);
    }

    private void RebuildMapMarkers(IReadOnlyList<JournalSpot> spots)
    {
        if (mapContent is null) return;
        foreach (var icon in mapMarkers.Keys) icon.Dispose();
        mapMarkers.Clear();
        foreach (JournalSpot spot in spots)
        {
            if (spot.MapPixelPosition is not Vector2 pos) continue;
            var icon = new IconImageNode
            {
                IconId = MapMarkerIconId,
                Size = new Vector2(MapMarkerSize, MapMarkerSize),
                Position = pos - new Vector2(MapMarkerSize / 2.0f, MapMarkerSize / 2.0f),
                Alpha = spot.IsUnlocked ? 1.0f : 0.4f,
                TextTooltip = spot.IsUnlocked ? spot.Name : "Undiscovered",
            };
            icon.AttachNode(mapContent);
            mapMarkers.Add(icon, spot);
        }
    }

    private void CenterMapOn(Vector2 focusPixel)
    {
        float scale = MapScale();
        mapPanY = focusPixel.Y - (MapImageHeight / scale) / 2.0f;
        ClampMapPan();
    }

    private void ClampMapPan()
    {
        float scale = MapScale();
        float maxPanY = Math.Max(0.0f, 2048.0f - MapImageHeight / scale);
        mapPanY = Math.Clamp(mapPanY, 0.0f, maxPanY);
    }

    private void ApplyMapPan()
    {
        if (mapContent is null) return;
        float scale = MapScale();
        mapContent.Scale = new Vector2(scale, scale);
        mapContent.Position = new Vector2(0.0f, -mapPanY * scale);
    }

    // Jumps the journal straight to the region/area/fishing hole a map marker
    // was just clicked for. SelectRegion has its own logic for restoring
    // whatever spot was last viewed in that region, which would otherwise win
    // over the marker just clicked - SelectSpot is called again afterward so
    // the click always wins.
    private void NavigateToSpot(JournalSpot spot)
    {
        if (!spot.IsUnlocked) return;
        JournalRegion? region = regions.FirstOrDefault(r => r.Areas.Any(a => a.Spots.Any(s => s.Id == spot.Id)));
        JournalArea? area = region?.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == spot.Id));
        if (region is null || area is null) return;
        string areaKey = $"{region.Name}\n{area.Name}";
        sessionState.ExpandedAreas.Add(areaKey);
        if (!ReferenceEquals(selectedRegion, region))
        {
            sessionState.SelectedSpot = spot.Id;
            SelectRegion(region);
        }
        SelectSpot(spot);
        ShowAreaMap(area);
    }

    private void LayoutMapPanel()
    {
        if (mapToggleButton is not null) mapToggleButton.IsVisible = !GuideMode;
        bool mapOn = MapEnabled;
        if (mapAreaName is not null) mapAreaName.IsVisible = mapOn;
        if (mapDiscoveredLabel is not null) mapDiscoveredLabel.IsVisible = mapOn;
        if (mapClip is not null) mapClip.IsVisible = mapOn;
        if (GuideMode) return;

        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        float maximumAreaWidth = Math.Max(240.0f, ContentSize.X - regionWidth - 380.0f);
        float areaWidth = Math.Clamp(areaWidthSetting, 240.0f, Math.Min(500.0f, maximumAreaWidth));
        float areaX = regionWidth + ColumnGap;
        float top = HeaderHeight + (areaList?.Height ?? 0.0f) + MapPanelSpacing;

        if (mapToggleButton is not null)
            mapToggleButton.Position = contentOrigin + new Vector2(areaX + areaWidth - MapToggleSize, top);
        if (!mapOn) return;

        if (mapAreaName is not null)
        {
            mapAreaName.Position = contentOrigin + new Vector2(areaX, top);
            mapAreaName.Width = Math.Max(40.0f, areaWidth - MapToggleSize - 4.0f);
        }
        if (mapDiscoveredLabel is not null)
        {
            mapDiscoveredLabel.Position = contentOrigin + new Vector2(areaX, top + 18.0f);
            mapDiscoveredLabel.Width = areaWidth;
        }
        if (mapClip is not null)
        {
            mapClip.Position = contentOrigin + new Vector2(areaX, top + MapCaptionHeight - MapPanelSpacing);
            mapClip.Size = new Vector2(areaWidth, MapImageHeight);
        }
        ClampMapPan();
        ApplyMapPan();
    }

    // Manual drag-to-pan and marker click handling, called from OnDraw every
    // frame - the map has no ImGui behind it, so hit-testing is done by hand
    // the same way the column dividers and fish list already are.
    private unsafe void UpdateMapInteraction(AtkUnitBase* addon, CursorInputData mouse, AtkStage* stage)
    {
        if (mapClip is null || mapContent is null || !MapEnabled || GuideMode) return;
        float scale = Math.Max(0.1f, addon->Scale);
        bool overAddon = stage != null && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon;

        if (draggingMap)
        {
            if (!mouse.IsGameWindowFocused || (mouse.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) == 0)
            {
                bool wasClick = mapDragDistance < 4.0f;
                draggingMap = false;
                if (wasClick) TryClickMarker(mouse, scale);
                return;
            }
            float deltaScreen = mouse.PositionY - mapDragStartMouseY;
            mapDragDistance = Math.Max(mapDragDistance, Math.Abs(deltaScreen));
            mapPanY = mapDragStartPanY - deltaScreen / (scale * MapScale());
            ClampMapPan();
            ApplyMapPan();
            return;
        }

        if (!overAddon || (mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) == 0)
            return;
        Vector2 origin = mapClip.ScreenPosition;
        float x = (mouse.PositionX - origin.X) / scale;
        float y = (mouse.PositionY - origin.Y) / scale;
        if (x < 0 || y < 0 || x >= mapClip.Width || y >= mapClip.Height)
            return;
        draggingMap = true;
        mapDragDistance = 0.0f;
        mapDragStartMouseY = mouse.PositionY;
        mapDragStartPanY = mapPanY;
    }

    private unsafe void TryClickMarker(CursorInputData mouse, float scale)
    {
        foreach (var (icon, spot) in mapMarkers)
        {
            if (!icon.IsVisible) continue;
            Vector2 position = icon.ScreenPosition;
            Vector2 size = icon.Size * scale;
            if (mouse.PositionX >= position.X && mouse.PositionX <= position.X + size.X
                && mouse.PositionY >= position.Y && mouse.PositionY <= position.Y + size.Y)
            {
                NavigateToSpot(spot);
                return;
            }
        }
    }

    private void DisposeMapPanel()
    {
        foreach (var icon in mapMarkers.Keys) icon.Dispose();
        mapMarkers.Clear();
        mapImage = null;
        mapContent = null;
        mapClip = null;
        mapAreaName = null;
        mapDiscoveredLabel = null;
        mapToggleButton = null;
        mapArea = null;
        mapLoadedTexturePath = null;
        draggingMap = false;
    }
}
