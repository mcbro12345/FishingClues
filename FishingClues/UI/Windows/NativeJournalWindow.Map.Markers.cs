using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// hole markers: fish icon, range circle, tooltip
public sealed partial class NativeJournalWindow
{
    private const float MapMarkerSize = 30.0f;
    // circle radius in map px = sheet Radius / this
    private const float RadiusToMapPixelDivisor = 7.0f;
    private const float MinCircleRawDiameter = 40.0f;
    private const uint FishMapMarkerIconId = 60929;

    // icon on the unscaled mapClip layer, circle on the scaled mapContent layer
    private readonly Dictionary<ImGuiImageNode, (JournalSpot Spot, ImGuiImageNode Circle)> mapMarkers = new();
    // one tooltip per marker, sharing one flashed the old name
    private readonly Dictionary<ImGuiImageNode, BackgroundTextNode> markerTooltips = new();
    private readonly HashSet<BackgroundTextNode> measuredTooltips = new();
    private ImGuiImageNode? hoveredMarker;

    private static float MarkerCircleRawDiameter(JournalSpot spot)
        => Math.Max(MinCircleRawDiameter, spot.Radius / RadiusToMapPixelDivisor * 2.0f);

    // keep markers for spots still wanted, new ones flash blank until the icon loads
    private void RebuildMapMarkers(IReadOnlyList<JournalSpot> spots)
    {
        if (mapClip is null || mapContent is null || markerLayer is null || tooltipLayer is null) return;
        var wanted = spots.Where(s => s.MapPixelPosition is not null).ToDictionary(s => s.Id);

        foreach (var (icon, data) in mapMarkers.ToList())
        {
            if (wanted.ContainsKey(data.Spot.Id)) continue;
            RemoveMarker(icon, data.Circle);
        }

        var shown = mapMarkers.Values.Select(m => m.Spot.Id).ToHashSet();
        foreach (JournalSpot spot in wanted.Values)
        {
            if (shown.Contains(spot.Id)) continue;
            // circle first so it's behind the icon
            var circle = new ImGuiImageNode { FitTexture = true };
            circle.AttachNode(mapContent);
            circle.LoadTexture(SharedAreaCircleTexture());
            circle.TextureSize = new Vector2(MapAreaCircleTextureSize, MapAreaCircleTextureSize);
            var icon = new ImGuiImageNode
            {
                Size = new Vector2(MapMarkerSize, MapMarkerSize),
                FitTexture = true,
                Alpha = 0.0f,
            };
            icon.AttachNode(markerLayer);
            mapMarkers.Add(icon, (spot, circle));
            _ = LoadGameIconAsync(icon, FishMapMarkerIconId, () => mapMarkers.ContainsKey(icon));
            var tooltip = new BackgroundTextNode
            {
                FontType = FontType.Axis,
                FontSize = 14,
                Size = new Vector2(200.0f, 24.0f),
                String = spot.Name,
                IsVisible = false,
                Alpha = 0.0f,
                Position = OffscreenTooltipPosition,
            };
            tooltip.AttachNode(tooltipLayer);
            markerTooltips.Add(icon, tooltip);
        }
        if (playerMarker is null) CreatePlayerMarker();
        UpdateMarkerLayout();
    }

    private void RemoveMarker(ImGuiImageNode icon, ImGuiImageNode circle)
    {
        if (ReferenceEquals(hoveredMarker, icon)) hoveredMarker = null;
        if (markerTooltips.Remove(icon, out var tooltip))
        {
            measuredTooltips.Remove(tooltip);
            tooltip.Dispose();
        }
        mapMarkers.Remove(icon);
        icon.Dispose();
        circle.Dispose();
    }

    private static readonly Vector2 OffscreenTooltipPosition = new(-10000.0f, -10000.0f);

    // mapClip clips drawing but not hit-testing, so visibility is tracked by hand
    private void UpdateMarkerLayout()
    {
        if (mapClip is null || mapContent is null) return;
        float scale = MapScale();
        Vector2 pan = new(mapPanX, mapPanY);
        Vector2 iconHalf = new(MapMarkerSize / 2.0f, MapMarkerSize / 2.0f);
        (float viewLeft, float viewTop, float viewWidth, float viewHeight) = CurrentMapView();
        foreach (var (icon, data) in mapMarkers)
        {
            if (data.Spot.MapPixelPosition is not Vector2 pixel)
            {
                icon.IsVisible = false;
                data.Circle.IsVisible = false;
                continue;
            }
            Vector2 iconLocal = (pixel - pan) * scale - iconHalf;
            icon.Position = iconLocal;
            icon.IsVisible = iconLocal.X + MapMarkerSize >= 0.0f && iconLocal.Y + MapMarkerSize >= 0.0f
                && iconLocal.X <= mapClip.Width && iconLocal.Y <= mapClip.Height;

            float circleRaw = MarkerCircleRawDiameter(data.Spot);
            data.Circle.Size = new Vector2(circleRaw, circleRaw);
            data.Circle.Position = pixel - new Vector2(circleRaw / 2.0f);
            data.Circle.IsVisible = pixel.X + circleRaw / 2.0f >= viewLeft && pixel.X - circleRaw / 2.0f <= viewLeft + viewWidth
                && pixel.Y + circleRaw / 2.0f >= viewTop && pixel.Y - circleRaw / 2.0f <= viewTop + viewHeight;
        }
        unsafe { UpdatePlayerMarker(); }
        RefreshMarkerTooltip();
    }

    private void RefreshMarkerTooltip()
    {
        if (mapClip is null) return;
        foreach (var (icon, tip) in markerTooltips)
            if (!ReferenceEquals(icon, hoveredMarker) && (tip.IsVisible || tip.Alpha > 0.0f))
            {
                tip.IsVisible = false;
                tip.Alpha = 0.0f;
                tip.Position = OffscreenTooltipPosition;
            }
        if (hoveredMarker is null || !hoveredMarker.IsVisible || !markerTooltips.TryGetValue(hoveredMarker, out var tooltip))
            return;
        if (!measuredTooltips.Contains(tooltip)) return;
        Vector2 size = tooltip.Size;
        Vector2 iconPos = hoveredMarker.Position;
        float x = iconPos.X + MapMarkerSize / 2.0f - size.X / 2.0f;
        float y = iconPos.Y - size.Y - 2.0f;
        if (y < 0.0f) y = iconPos.Y + MapMarkerSize + 2.0f;
        x = Math.Clamp(x, 0.0f, Math.Max(0.0f, mapClip.Width - size.X));
        tooltip.Position = new Vector2(x, y);
        tooltip.Alpha = 1.0f;
        tooltip.IsVisible = true;
    }

    // text can only be measured a few frames after creation
    private void MeasurePendingTooltips()
    {
        if (measuredTooltips.Count >= markerTooltips.Count) return;
        foreach (var tip in markerTooltips.Values)
        {
            if (measuredTooltips.Contains(tip)) continue;
            Vector2 text = tip.TextNode.GetTextDrawSize(false);
            if (text.X < 1.0f) continue;
            tip.Size = new Vector2(text.X + 20.0f, Math.Max(text.Y + 8.0f, 24.0f));
            measuredTooltips.Add(tip);
        }
    }

    private ImGuiImageNode? MarkerAt(CursorInputData mouse, float scale)
    {
        foreach (var (icon, _) in mapMarkers)
        {
            if (!icon.IsVisible) continue;
            Vector2 position = icon.ScreenPosition;
            Vector2 size = icon.Size * scale;
            if (mouse.PositionX >= position.X && mouse.PositionX <= position.X + size.X
                && mouse.PositionY >= position.Y && mouse.PositionY <= position.Y + size.Y)
                return icon;
        }
        return null;
    }

    private void TryClickMarker(CursorInputData mouse, float scale)
    {
        if (MarkerAt(mouse, scale) is not { } icon || !mapMarkers.TryGetValue(icon, out var data)) return;
        PlayClickSound();
        NavigateToSpot(data.Spot);
    }
}
