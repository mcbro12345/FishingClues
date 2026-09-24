using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace FishingClues.UI.Windows;

// map pan/zoom and mouse handling
public sealed partial class NativeJournalWindow
{
    private const float ClickDragThreshold = 4.0f;

    private bool draggingMap;
    private float mapDragStartMouseX;
    private float mapDragStartMouseY;
    private float mapDragStartPanX;
    private float mapDragStartPanY;
    private float mapDragDistance;

    // first centered before the clip has a size, so the first layout centers again
    private Vector2? lastCenteredFocus;
    private bool mapInitialCenterPending = true;

    private float BaselineMapScale() => EffectiveMapWidth(areaWidthSetting) / 2048.0f;

    private float MapScale() => Math.Max(0.005f, BaselineMapScale() * mapZoom);

    private float MapClipWidth => mapClip?.Width ?? EffectiveMapWidth(areaWidthSetting);
    private float MapClipHeight => mapClip?.Height ?? EffectiveMapHeight();

    private (float Left, float Top, float Width, float Height) CurrentMapView()
    {
        float scale = MapScale();
        return (mapPanX, mapPanY, MapClipWidth / scale, MapClipHeight / scale);
    }

    private void CenterMapOn(Vector2 focusPixel)
    {
        // fully zoomed out shows the whole map, centered like the game's
        if (mapZoom <= MinMapZoom + 0.001f) focusPixel = new Vector2(1024.0f, 1024.0f);
        lastCenteredFocus = focusPixel;
        float scale = MapScale();
        mapPanX = focusPixel.X - MapClipWidth / scale / 2.0f;
        mapPanY = focusPixel.Y - MapClipHeight / scale / 2.0f;
        ClampMapPan();
    }

    private void ClampMapPan()
    {
        float scale = MapScale();
        bool zoomedOut = mapZoom <= MinMapZoom + 0.001f;
        (float minPanX, float maxPanX) = ClampedPanRange(MapClipWidth / scale, zoomedOut);
        (float minPanY, float maxPanY) = ClampedPanRange(MapClipHeight / scale, zoomedOut);
        mapPanX = Math.Clamp(mapPanX, minPanX, maxPanX);
        mapPanY = Math.Clamp(mapPanY, minPanY, maxPanY);
    }

    // zoomed in you can pan half a view past the edges, zoomed out it stays inside so no black shows. A view that fits has no room to move
    private static (float Min, float Max) ClampedPanRange(float viewSize, bool keepInsideMap)
    {
        float freeSpace = 2048.0f - viewSize;
        if (freeSpace <= 0.0f)
        {
            float centered = freeSpace / 2.0f;
            return (centered, centered);
        }
        return keepInsideMap ? (0.0f, freeSpace) : (-viewSize / 2.0f, freeSpace + viewSize / 2.0f);
    }

    private void ApplyMapPan()
    {
        if (mapContent is null) return;
        float scale = MapScale();
        mapContent.Scale = new Vector2(scale, scale);
        mapContent.Position = new Vector2(-mapPanX * scale, -mapPanY * scale);
        UpdateMarkerLayout();
    }

    // per frame, hit-testing is by hand
    private unsafe void UpdateMapInteraction(AtkUnitBase* addon, CursorInputData mouse, AtkStage* stage)
    {
        if (mapClip is null || mapContent is null || !MapEnabled || GuideMode) return;
        MeasurePendingTooltips();
        UpdatePlayerMarker();
        float scale = Math.Max(0.1f, addon->Scale);
        bool overAddon = IsOverAddon(addon, stage);

        if (draggingMap)
        {
            if (!mouse.IsGameWindowFocused || (mouse.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) == 0)
            {
                bool wasClick = mapDragDistance < ClickDragThreshold;
                draggingMap = false;
                if (wasClick) TryClickMarker(mouse, scale);
                return;
            }
            float deltaScreenX = mouse.PositionX - mapDragStartMouseX;
            float deltaScreenY = mouse.PositionY - mapDragStartMouseY;
            mapDragDistance = Math.Max(mapDragDistance, Math.Max(Math.Abs(deltaScreenX), Math.Abs(deltaScreenY)));
            // hoveredMarker stays as it was when the drag began, so its tooltip follows the map.
            float contentScale = MapScale();
            mapPanX = mapDragStartPanX - deltaScreenX / (scale * contentScale);
            mapPanY = mapDragStartPanY - deltaScreenY / (scale * contentScale);
            ClampMapPan();
            ApplyMapPan();
            return;
        }

        if (!overAddon)
        {
            if (hoveredMarker is not null) { hoveredMarker = null; RefreshMarkerTooltip(); }
            return;
        }

        Vector2 origin = mapClip.ScreenPosition;
        float localX = (mouse.PositionX - origin.X) / scale;
        float localY = (mouse.PositionY - origin.Y) / scale;
        bool overMap = localX >= 0 && localY >= 0 && localX < mapClip.Width && localY < mapClip.Height;

        ImGuiImageNode? hovered = overMap ? MarkerAt(mouse, scale) : null;
        if (!ReferenceEquals(hovered, hoveredMarker))
        {
            hoveredMarker = hovered;
            RefreshMarkerTooltip();
        }

        if (overMap && mouse.MouseWheel != 0)
        {
            // Zoom around the cursor: the map point under it stays under it.
            float scaleBefore = MapScale();
            Vector2 mapPointUnderCursor = new(mapPanX + localX / scaleBefore, mapPanY + localY / scaleBefore);
            mapZoom = Math.Clamp(mapZoom * (mouse.MouseWheel > 0 ? MapZoomStep : 1.0f / MapZoomStep), MinMapZoom, MaxMapZoom);
            float scaleAfter = MapScale();
            mapPanX = mapPointUnderCursor.X - localX / scaleAfter;
            mapPanY = mapPointUnderCursor.Y - localY / scaleAfter;
            ClampMapPan();
            ApplyMapPan();
        }

        if (overMap && (mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0)
        {
            draggingMap = true;
            mapDragDistance = 0.0f;
            mapDragStartMouseX = mouse.PositionX;
            mapDragStartMouseY = mouse.PositionY;
            mapDragStartPanX = mapPanX;
            mapDragStartPanY = mapPanY;
        }
    }
}
