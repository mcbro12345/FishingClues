using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// The area map under the area list: the zone's own map with a marker per
// discovered fishing hole. This file holds the state and the panel's lifecycle;
// see the other NativeJournalWindow.Map.*.cs files for textures, markers, the
// player marker, pan/zoom and layout.
public sealed partial class NativeJournalWindow
{
    private const float MapDividerHeight = 10.0f;
    private const float MapCaptionHeight = 40.0f;
    private const float MapImageHeight = 150.0f;
    private const float MapImageHeightOffset = 105.0f;
    private const float MapImageWidthOffset = -5.0f;
    private const float MapPanelSpacing = 8.0f;

    private const float MinMapZoom = 1.0f;
    private const float MaxMapZoom = 15.0f;
    private const float MapZoomStep = 1.25f;
    // A hole picked from the list is zoomed so its range circle fills this
    // fraction of the map view's shorter side.
    private const float MapCircleFitFraction = 0.7f;

    private ResNode? mapClip;
    private ResNode? mapContent;
    // Draw order above the map: hole icons, then the player marker, then tooltips. Each has its own
    // layer so a marker added later still draws in the right place.
    private ResNode? markerLayer;
    private ResNode? playerLayer;
    private ResNode? tooltipLayer;
    private ImGuiImageNode? mapImage;
    private ImGuiImageNode? mapBackdrop;
    private string? mapLoadedTexturePath;
    private HorizontalLineNode? mapCaptionDivider;
    private LabelTextNode? mapAreaName;
    private LabelTextNode? mapDiscoveredLabel;
    private TextureButtonNode? mapToggleButton;
    private ImGuiImageNode? mapBorderTop;
    private ImGuiImageNode? mapBorderBottom;
    private ImGuiImageNode? mapBorderLeft;
    private ImGuiImageNode? mapBorderRight;

    // mapArea is the area of the last hole actually picked; previewArea is only
    // tracked for the generic backdrop shown before any hole has been picked.
    private JournalArea? mapArea;
    private JournalArea? previewArea;

    private float mapPanX;
    private float mapPanY;
    private float mapZoom = MinMapZoom;
    // True from opening the window until the first area map is shown, which
    // restores the zoom and pan saved when the journal was last closed.
    private bool restoreMapView;
    private bool mapPanelVisible = true;

    // A hole picked while the map was hidden, applied when it is shown again.
    private bool hasPendingMapFocus;
    private JournalSpot? pendingMapFocusSpot;
    private bool pendingMapZoomToSpot;
    private bool pendingMapAreaChanged;

    private bool MapEnabled => !GuideMode && configuration.ShowAreaLocationMap && mapPanelVisible;

    // The map's space is taken from the area list above it.
    private float ReservedMapHeight() => MapEnabled
        ? MapDividerHeight + MapCaptionHeight + EffectiveMapHeight() + MapPanelSpacing
        : 0.0f;

    private float EffectiveMapWidth(float areaWidth) => Math.Max(60.0f, areaWidth + MapImageWidthOffset);
    private float EffectiveMapHeight() => Math.Max(60.0f, MapImageHeight + MapImageHeightOffset);

    private void BuildMapPanel()
    {
        mapPanelVisible = configuration.MapPanelOpen;
        mapZoom = MinMapZoom;
        restoreMapView = sessionState.MapZoom > 0.0f;

        mapCaptionDivider = new HorizontalLineNode { Height = 2.0f };
        mapCaptionDivider.AttachNode(this);
        mapAreaName = new LabelTextNode { Height = 18.0f, FontSize = 14, String = "" };
        mapAreaName.AttachNode(this);
        mapDiscoveredLabel = new LabelTextNode { Height = 16.0f, FontSize = 12, String = "" };
        mapDiscoveredLabel.AttachNode(this);

        mapClip = new ResNode { NodeFlags = NodeFlags.Clip | NodeFlags.Visible };
        mapClip.AttachNode(this);
        mapBackdrop = new ImGuiImageNode { FitTexture = true, Alpha = 0.0f };
        mapBackdrop.LoadTexture(CreateBackdropTexture());
        mapBackdrop.TextureSize = new Vector2(MapBackdropTextureSize, MapBackdropTextureSize);
        mapBackdrop.AttachNode(mapClip);
        mapContent = new ResNode();
        mapContent.AttachNode(mapClip);
        markerLayer = new ResNode();
        markerLayer.AttachNode(mapClip);
        playerLayer = new ResNode();
        playerLayer.AttachNode(mapClip);
        tooltipLayer = new ResNode();
        tooltipLayer.AttachNode(mapClip);
        // Image nodes without a texture draw black, so they start transparent.
        mapImage = new ImGuiImageNode { Size = new Vector2(2048.0f, 2048.0f), Alpha = 0.0f };
        mapImage.AttachNode(mapContent);

        mapBorderTop = CreateBorderNode(vertical: false, outerFirst: true);
        mapBorderBottom = CreateBorderNode(vertical: false, outerFirst: false);
        mapBorderLeft = CreateBorderNode(vertical: true, outerFirst: true);
        mapBorderRight = CreateBorderNode(vertical: true, outerFirst: false);

        mapToggleButton = new TextureButtonNode
        {
            Size = new Vector2(CornerButtonSize, CornerButtonSize),
            TexturePath = "ui/uld/AreaMap.tex",
            TextureCoordinates = new Vector2(144.0f, 112.0f),
            TextureSize = new Vector2(28.0f, 28.0f),
            OnClick = ToggleMapVisibility,
        };
        mapToggleButton.AttachNode(this);
    }

    private ImGuiImageNode CreateBorderNode(bool vertical, bool outerFirst)
    {
        // Each node owns (and disposes) its texture, so they can't share one.
        var border = new ImGuiImageNode { FitTexture = true };
        border.LoadTexture(CreateBronzeBorderTexture(vertical, outerFirst));
        border.TextureSize = new Vector2(MapBorderPatternSize, MapBorderPatternSize);
        border.AttachNode(this);
        return border;
    }

    private void ToggleMapVisibility()
    {
        mapPanelVisible = !mapPanelVisible;
        configuration.MapPanelOpen = mapPanelVisible;
        options.SaveLayout();
        // While hidden the map skips its texture and marker work, so catch up
        // now. Zoom and pan are untouched while hidden and come back as left.
        if (mapPanelVisible)
        {
            if (mapArea is not null && hasPendingMapFocus)
            {
                hasPendingMapFocus = false;
                restoreMapView = false;
                if (pendingMapAreaChanged) mapZoom = MinMapZoom;
                pendingMapAreaChanged = false;
                ShowAreaMap(mapArea, pendingMapFocusSpot, pendingMapZoomToSpot);
                pendingMapFocusSpot = null;
            }
            else if (mapArea is not null) ShowAreaMap(mapArea, preserveView: true);
            else
            {
                mapZoom = MinMapZoom;
                RefreshMapPreview(previewArea);
            }
        }
        LayoutAttachedNodes();
    }

    // Before any hole has been picked the panel shows the whole-continent map
    // with no markers, so it doesn't start out empty.
    private void RefreshMapPreview(JournalArea? area)
    {
        previewArea = area;
        if (mapAreaName is not null) mapAreaName.String = "Select a fishing hole.";
        if (mapDiscoveredLabel is not null) mapDiscoveredLabel.String = "";
        if (!MapEnabled) return;

        EnsureMapTexture(WorldMapTexturePath());
        RebuildMapMarkers(Array.Empty<JournalSpot>());
        CenterMapOn(new Vector2(1024.0f, 1024.0f));
        ApplyMapPan();
    }

    // Called when a hole is picked, from the area list or a marker click.
    // Expanding an area or switching region leaves the map alone.
    private void ShowAreaMap(JournalArea area, JournalSpot? focusSpot = null, bool zoomToSpot = false, bool preserveView = false)
    {
        bool areaChanged = !ReferenceEquals(mapArea, area);
        mapArea = area;
        if (mapAreaName is not null) mapAreaName.String = area.Name;
        if (mapDiscoveredLabel is not null)
            mapDiscoveredLabel.String = $"Locations Discovered: {area.Spots.Count(s => s.IsUnlocked)}/{area.Spots.Count}";
        if (!MapEnabled)
        {
            hasPendingMapFocus = true;
            pendingMapFocusSpot = focusSpot;
            pendingMapZoomToSpot = zoomToSpot;
            pendingMapAreaChanged |= areaChanged;
            return;
        }

        if (areaChanged) mapZoom = MinMapZoom;

        var withMap = area.Spots.Where(s => s.MapPixelPosition is not null).ToArray();
        // Undiscovered holes get no pin, which would give away where they are.
        var discoveredWithMap = withMap.Where(s => s.IsUnlocked).ToArray();
        Services.Log.Debug($"[FishingClues] Area map: '{area.Name}' has {area.Spots.Count} spot(s), "
            + $"{withMap.Length} with a map position ({discoveredWithMap.Length} discovered); texture='{withMap.FirstOrDefault()?.MapTexturePath}'");
        EnsureMapTexture(withMap.FirstOrDefault()?.MapTexturePath);
        RebuildMapMarkers(discoveredWithMap);

        Vector2 defaultFocus = AreaFocusPixel(discoveredWithMap) ?? new Vector2(1024.0f, 1024.0f);
        bool restoredView = false;
        if (restoreMapView)
        {
            restoreMapView = false;
            if (sessionState.MapArea == area.Name && sessionState.MapZoom > 0.0f)
            {
                mapZoom = Math.Clamp(sessionState.MapZoom, MinMapZoom, MaxMapZoom);
                mapPanX = sessionState.MapPanX;
                mapPanY = sessionState.MapPanY;
                ClampMapPan();
                restoredView = true;
                lastCenteredFocus = null;
            }
        }
        if (!restoredView && preserveView)
        {
            ClampMapPan();
        }
        else if (!restoredView)
        {
            if (zoomToSpot && focusSpot?.MapPixelPosition is not null)
            {
                float viewSize = Math.Min(mapClip?.Width ?? EffectiveMapWidth(areaWidthSetting), mapClip?.Height ?? EffectiveMapHeight());
                float circleOnScreenAtFit = MarkerCircleRawDiameter(focusSpot) * BaselineMapScale();
                float wantedCircleSize = viewSize * MapCircleFitFraction;
                mapZoom = Math.Clamp(wantedCircleSize / Math.Max(0.01f, circleOnScreenAtFit), MinMapZoom, MaxMapZoom);
            }
            CenterMapOn(focusSpot?.MapPixelPosition ?? defaultFocus);
        }
        ApplyMapPan();
    }

    // The middle of the box containing every spot, or null if none has a position.
    private static Vector2? AreaFocusPixel(IReadOnlyList<JournalSpot> spots)
    {
        var pixels = spots.Where(s => s.MapPixelPosition is not null).Select(s => s.MapPixelPosition!.Value).ToArray();
        if (pixels.Length == 0) return null;
        return new Vector2((pixels.Min(p => p.X) + pixels.Max(p => p.X)) / 2.0f,
            (pixels.Min(p => p.Y) + pixels.Max(p => p.Y)) / 2.0f);
    }

    // Jumps to the region, area and hole a marker was clicked for.
    private void NavigateToSpot(JournalSpot spot, bool zoomToSpot = false)
    {
        if (!spot.IsUnlocked) return;
        JournalRegion? region = regions.FirstOrDefault(r => r.Areas.Any(a => a.Spots.Any(s => s.Id == spot.Id)));
        JournalArea? area = region?.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == spot.Id));
        if (region is null || area is null) return;
        if (!ReferenceEquals(selectedRegion, region))
        {
            sessionState.SelectedSpot = spot.Id;
            SelectRegion(region, forceOpenArea: area, forceSelectSpot: spot);
        }
        SelectSpot(spot, zoomToSpot);
    }

    // The area map's part of the diagnostics report.
    public string DescribeMapState()
    {
        var report = new System.Text.StringBuilder();
        if (GuideMode || mapArea is null)
        {
            report.AppendLine("Area map: no area currently shown in this journal window.");
            return report.ToString();
        }

        var withMap = mapArea.Spots.Where(s => s.MapPixelPosition is not null).ToArray();
        report.AppendLine($"Area map: area='{mapArea.Name}', {mapArea.Spots.Count} spot(s), {withMap.Length} with a map position");
        report.AppendLine($"Area map: texture='{mapLoadedTexturePath}', clip={mapClip?.Width ?? 0}x{mapClip?.Height ?? 0}, "
            + $"pan=({mapPanX:0.#},{mapPanY:0.#}), zoom={mapZoom:0.##}, scale={MapScale():0.####}");
        foreach (JournalSpot spot in mapArea.Spots)
        {
            ImGuiImageNode? marker = mapMarkers.FirstOrDefault(pair => pair.Value.Spot.Id == spot.Id).Key;
            report.AppendLine($"Area map: spot='{spot.Name}', unlocked={spot.IsUnlocked}, "
                + $"pixel={(spot.MapPixelPosition is Vector2 pixel ? pixel.ToString() : "none")}, "
                + $"marker={(marker is null ? "not built" : $"local={marker.Position}, visible={marker.IsVisible}")}");
        }
        return report.ToString();
    }

    private void DisposeMapPanel()
    {
        DisposeMarkers();
        mapImage = null;
        mapBackdrop = null;
        mapContent = null;
        markerLayer = playerLayer = tooltipLayer = null;
        mapClip = null;
        mapCaptionDivider = null;
        mapAreaName = null;
        mapDiscoveredLabel = null;
        mapToggleButton = null;
        mapBorderTop = null;
        mapBorderBottom = null;
        mapBorderLeft = null;
        mapBorderRight = null;
        mapArea = null;
        previewArea = null;
        mapLoadedTexturePath = null;
        draggingMap = false;
    }
}
