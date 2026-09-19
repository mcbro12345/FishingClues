using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace FishingClues.UI.Windows;

// The map's pan and zoom: the maths, and the mouse handling for dragging,
// scrolling and clicking markers.
public sealed partial class NativeJournalWindow
{
    private const float ClickDragThreshold = 4.0f;

    private bool draggingMap;
    private float mapDragStartMouseX;
    private float mapDragStartMouseY;
    private float mapDragStartPanX;
    private float mapDragStartPanY;
    private float mapDragDistance;

    // The map is first centered while the window is still being built, before
    // the clip has a size, so that centering lands off-center. The first layout
    // that sizes the clip centers on the same point again.
    private Vector2? lastCenteredFocus;
    private bool mapInitialCenterPending = true;

    // Screen pixels per map pixel with the whole 2048px map fitted to the panel width.
    private float BaselineMapScale() => EffectiveMapWidth(areaWidthSetting) / 2048.0f;

    private float MapScale() => Math.Max(0.005f, BaselineMapScale() * mapZoom);

    private float MapClipWidth => mapClip?.Width ?? EffectiveMapWidth(areaWidthSetting);
    private float MapClipHeight => mapClip?.Height ?? EffectiveMapHeight();

    // The visible part of the map, in map pixels.
    private (float Left, float Top, float Width, float Height) CurrentMapView()
    {
        float scale = MapScale();
        return (mapPanX, mapPanY, MapClipWidth / scale, MapClipHeight / scale);
    }

    private void CenterMapOn(Vector2 focusPixel)
    {
        // Zoomed all the way out the whole map is on show, so it is centered
        // like the game's own map instead of following a hole.
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
        (float minPanX, float maxPanX) = ClampedPanRange(MapClipWidth / scale);
        (float minPanY, float maxPanY) = ClampedPanRange(MapClipHeight / scale);
        mapPanX = Math.Clamp(mapPanX, minPanX, maxPanX);
        mapPanY = Math.Clamp(mapPanY, minPanY, maxPanY);
    }

    // A view smaller than the 2048px map may pan half a view past each edge, so
    // any point can be brought to the middle. One that already fits the whole
    // map has no room to move and stays centered.
    private static (float Min, float Max) ClampedPanRange(float viewSize)
    {
        float freeSpace = 2048.0f - viewSize;
        if (freeSpace <= 0.0f)
        {
            float centered = freeSpace / 2.0f;
            return (centered, centered);
        }
        return (-viewSize / 2.0f, freeSpace + viewSize / 2.0f);
    }

    private void ApplyMapPan()
    {
        if (mapContent is null) return;
        float scale = MapScale();
        mapContent.Scale = new Vector2(scale, scale);
        mapContent.Position = new Vector2(-mapPanX * scale, -mapPanY * scale);
        UpdateMarkerLayout();
    }

    // Called every frame from OnDraw. The map has no ImGui behind it, so
    // hit-testing is done by hand.
    private unsafe void UpdateMapInteraction(AtkUnitBase* addon, CursorInputData mouse, AtkStage* stage)
    {
        if (mapClip is null || mapContent is null || !MapEnabled || GuideMode) return;
        MeasurePendingTooltips();
        UpdatePlayerMarker();
        float scale = Math.Max(0.1f, addon->Scale);
        bool overAddon = stage != null && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon;

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
