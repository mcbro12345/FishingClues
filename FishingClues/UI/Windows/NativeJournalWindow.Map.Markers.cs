using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// The fishing hole markers: a fish icon, a range circle behind it, and a tooltip.
public sealed partial class NativeJournalWindow
{
    // Fish icon size in screen pixels, whatever the zoom.
    private const float MapMarkerSize = 30.0f;
    // The sheet's fishing spot Radius divided by this is the circle's radius in map pixels.
    private const float RadiusToMapPixelDivisor = 7.0f;
    private const float MinCircleRawDiameter = 40.0f;
    private const uint FishMapMarkerIconId = 60929;

    // The icon is the key: it sits on the unscaled mapClip layer and is placed by
    // hand, while its circle is on the scaled mapContent layer, so the icon keeps
    // its size and the circle grows and shrinks with the zoom.
    private readonly Dictionary<ImGuiImageNode, (JournalSpot Spot, ImGuiImageNode Circle)> mapMarkers = new();
    // One pre-rendered tooltip per marker: swapping one shared node's text
    // flashed the previous name for a frame.
    private readonly Dictionary<ImGuiImageNode, BackgroundTextNode> markerTooltips = new();
    private readonly HashSet<BackgroundTextNode> measuredTooltips = new();
    private ImGuiImageNode? hoveredMarker;

    // The circle's diameter in map pixels.
    private static float MarkerCircleRawDiameter(JournalSpot spot)
        => Math.Max(MinCircleRawDiameter, spot.Radius / RadiusToMapPixelDivisor * 2.0f);

    // Brings the markers in line with the wanted spots. Markers for spots that are still wanted
    // stay as they are: a new marker draws blank until its icon loads, which showed as a flash
    // every time a marker was clicked.
    private void RebuildMapMarkers(IReadOnlyList<JournalSpot> spots)
    {
        if (mapClip is null || mapContent is null) return;
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
            // Circle first so it draws behind the icon.
            var circle = new ImGuiImageNode { FitTexture = true };
            circle.AttachNode(mapContent);
            circle.LoadTexture(CreateAreaCircleTexture());
            circle.TextureSize = new Vector2(MapAreaCircleTextureSize, MapAreaCircleTextureSize);
            var icon = new ImGuiImageNode
            {
                Size = new Vector2(MapMarkerSize, MapMarkerSize),
                FitTexture = true,
                Alpha = 0.0f,
            };
            icon.AttachNode(mapClip);
            mapMarkers.Add(icon, (spot, circle));
            _ = LoadMarkerIconAsync(icon);
            // The tooltip goes on last so it draws over the icons.
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
            tooltip.AttachNode(mapClip);
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

    private async Task LoadMarkerIconAsync(ImGuiImageNode icon)
    {
        IDalamudTextureWrap texture;
        try
        {
            texture = await Services.TextureProvider.GetFromGameIcon(FishMapMarkerIconId).RentAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "[FishingClues] Area map: failed to load a marker icon.");
            return;
        }

        await Services.Framework.Run(() =>
        {
            // The marker may have been torn down while the icon was loading.
            if (!mapMarkers.ContainsKey(icon))
            {
                texture.Dispose();
                return;
            }
            icon.LoadTexture(texture);
            icon.Alpha = 1.0f;
        });
    }

    // Repositions every marker for the current pan, zoom and column width.
    // Visibility is tracked by hand: mapClip's clip hides rendering but not
    // hit-testing, so an off-screen marker would stay clickable.
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
        // Same pass as the icons, so the player marker never trails the map by a frame.
        unsafe { UpdatePlayerMarker(); }
        RefreshMarkerTooltip();
    }

    // Shows the hovered marker's tooltip above its icon (below when there is no
    // room, kept inside the map) and hides all the others.
    private void RefreshMarkerTooltip()
    {
        if (mapClip is null) return;
        foreach (var (icon, tip) in markerTooltips)
            if (!ReferenceEquals(icon, hoveredMarker) && (tip.IsVisible || tip.Alpha > 0.0f))
            {
                // Hidden three ways at once so a late-applied change can't leave it showing.
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

    // Called every frame: sizes each hidden tooltip once its text can be
    // measured, a few frames after it was created.
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
        // the marker has no button of its own, so nothing plays a click sound unless this does
        unsafe { UIGlobals.PlaySoundEffect(UiClickSoundEffectId); }
        NavigateToSpot(data.Spot);
    }

    private void DisposeMarkers()
    {
        foreach (var (icon, data) in mapMarkers)
        {
            icon.Dispose();
            data.Circle.Dispose();
        }
        mapMarkers.Clear();
        DisposePlayerMarker();
        foreach (var tip in markerTooltips.Values) tip.Dispose();
        markerTooltips.Clear();
        measuredTooltips.Clear();
        hoveredMarker = null;
    }
}
