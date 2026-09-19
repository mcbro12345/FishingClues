using System.Numerics;

namespace FishingClues.UI.Windows;

// Positions the map panel: the toggle button, caption, map and border.
public sealed partial class NativeJournalWindow
{
    private const float MapDividerOffsetX = -2.0f;
    private const float MapDividerOffsetY = -8.0f;
    private const float MapTitleOffsetX = 2.0f;
    private const float MapTitleOffsetY = -9.0f;
    private const float MapBorderThickness = 6.0f;
    // How far the border sits outside the map's edge.
    private const float MapBorderInset = 3.0f;

    private void LayoutMapPanel()
    {
        if (mapToggleButton is not null)
        {
            mapToggleButton.IsVisible = !GuideMode && configuration.ShowAreaLocationMap;
            mapToggleButton.Position = MapButtonPosition();
            mapToggleButton.TextTooltip = mapPanelVisible ? "Hide the area map" : "Show the area map";
        }
        bool mapOn = MapEnabled;
        if (mapCaptionDivider is not null) mapCaptionDivider.IsVisible = mapOn;
        if (mapAreaName is not null) mapAreaName.IsVisible = mapOn;
        if (mapDiscoveredLabel is not null) mapDiscoveredLabel.IsVisible = mapOn;
        if (mapClip is not null) mapClip.IsVisible = mapOn;
        if (mapBorderTop is not null) mapBorderTop.IsVisible = mapOn;
        if (mapBorderBottom is not null) mapBorderBottom.IsVisible = mapOn;
        if (mapBorderLeft is not null) mapBorderLeft.IsVisible = mapOn;
        if (mapBorderRight is not null) mapBorderRight.IsVisible = mapOn;
        if (GuideMode) return;

        var (regionWidth, areaWidth) = ColumnWidths();
        float areaX = regionWidth + ColumnGap;

        // The map sits at the bottom of the content area, directly below the
        // area list, which is sized to leave exactly this much room (see ReservedMapHeight).
        float mapWidth = EffectiveMapWidth(areaWidth);
        float mapHeight = EffectiveMapHeight();
        float captionTop = ContentSize.Y - MapCaptionHeight - mapHeight;
        if (mapCaptionDivider is not null)
        {
            mapCaptionDivider.Position = contentOrigin + new Vector2(areaX + MapDividerOffsetX, captionTop - MapDividerHeight + MapDividerOffsetY);
            mapCaptionDivider.Width = areaWidth;
        }
        if (!mapOn) return;

        Vector2 titleOffset = new(MapTitleOffsetX, MapTitleOffsetY);
        if (mapAreaName is not null)
        {
            mapAreaName.Position = contentOrigin + new Vector2(areaX, captionTop) + titleOffset;
            mapAreaName.Width = areaWidth;
        }
        if (mapDiscoveredLabel is not null)
        {
            mapDiscoveredLabel.Position = contentOrigin + new Vector2(areaX, captionTop + 18.0f) + titleOffset;
            mapDiscoveredLabel.Width = areaWidth;
        }

        Vector2 clipPosition = contentOrigin + new Vector2(areaX, captionTop + MapCaptionHeight - MapPanelSpacing);
        if (mapClip is not null)
        {
            mapClip.Position = clipPosition;
            mapClip.Size = new Vector2(mapWidth, mapHeight);
            if (mapBackdrop is not null) mapBackdrop.Size = new Vector2(mapWidth, mapHeight);
            if (mapInitialCenterPending)
            {
                mapInitialCenterPending = false;
                if (lastCenteredFocus is Vector2 focus)
                {
                    CenterMapOn(focus);
                    ApplyMapPan();
                }
            }
        }
        LayoutMapBorder(clipPosition, new Vector2(mapWidth, mapHeight));

        ClampMapPan();
        ApplyMapPan();
    }

    // The four border lines run around the map with their corners overlapping.
    private void LayoutMapBorder(Vector2 clipPosition, Vector2 clipSize)
    {
        const float thickness = MapBorderThickness;
        const float inset = MapBorderInset;
        float fullWidth = clipSize.X + 2.0f * inset;
        float fullHeight = clipSize.Y + 2.0f * inset;
        if (mapBorderTop is not null)
        {
            mapBorderTop.Height = thickness;
            mapBorderTop.Width = fullWidth;
            mapBorderTop.Position = clipPosition + new Vector2(-inset, -inset);
        }
        if (mapBorderBottom is not null)
        {
            mapBorderBottom.Height = thickness;
            mapBorderBottom.Width = fullWidth;
            mapBorderBottom.Position = clipPosition + new Vector2(-inset, clipSize.Y + inset - thickness);
        }
        if (mapBorderLeft is not null)
        {
            mapBorderLeft.Width = thickness;
            mapBorderLeft.Height = fullHeight;
            mapBorderLeft.Position = clipPosition + new Vector2(-inset, -inset);
        }
        if (mapBorderRight is not null)
        {
            mapBorderRight.Width = thickness;
            mapBorderRight.Height = fullHeight;
            mapBorderRight.Position = clipPosition + new Vector2(clipSize.X + inset - thickness, -inset);
        }
    }
}
