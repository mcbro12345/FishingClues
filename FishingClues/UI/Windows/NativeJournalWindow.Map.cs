using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;

using MapSheet = Lumina.Excel.Sheets.Map;

namespace FishingClues.UI.Windows;

// The optional area map shown under the area list: a scaled-down copy of the
// zone's own map texture with a marker per fishing hole in the currently
// viewed area, pannable by dragging, zoomable with the scroll wheel, and
// clickable to jump straight to a hole.
public sealed partial class NativeJournalWindow
{
    // Extra headroom above the caption for the divider that separates the map
    // panel from the area list above it.
    private const float MapDividerHeight = 10.0f;
    private const float MapCaptionHeight = 40.0f;
    private const float MapImageHeight = 150.0f;
    private const float MapPanelSpacing = 8.0f;
    private const float MapToggleSize = 24.0f;
    // Wide enough for "Show Map"/"Hide Map" (see LayoutMapPanel), not just
    // the plain "Map" label this used to always show.
    private const float MapToggleButtonWidth = 82.0f;
    // The fish icon's own screen size - always this many pixels regardless of
    // the current map zoom (see UpdateMarkerLayout, which tracks the icon's
    // position by hand in mapClip's unscaled space for exactly this reason).
    // Unlike the range circle below, this one point has to stay reliably
    // clickable and identifiable at any zoom level, so it doesn't scale down
    // to the point of being hard to hit at the zoomed-out overview, or grow
    // to some huge size at max zoom.
    private const float MapMarkerSize = 30.0f;
    // The translucent range circle drawn behind each marker (see
    // CreateAreaCircleTexture) - unlike the fish icon, this one DOES scale
    // with zoom (it's parented under mapContent, not mapClip - see
    // RebuildMapMarkers) and DOES vary per spot, from its own
    // FishingSpot.Radius (see MarkerCircleRawDiameter), matching the vanilla
    // journal's own map showing differently-sized circles per hole.
    // An earlier version compressed Radius into an arbitrary decorative
    // Min/Max pixel band, which flattened the real proportions between spots
    // (e.g. Radius 600 vs 400, a genuine 1.5x difference, only came out as a
    // barely-visible ~1.27x). GatherBuddy (see GATHERBUDDY_LICENSE.txt,
    // vendored alongside this plugin) divides the raw sheet Radius by this
    // same factor to convert it into the fishing spot's own map-pixel
    // coordinate space - the same space MapPixelPosition already uses (see
    // JournalBuilder.BuildMapInfo's comment on FishingSpot.X/Z being raw
    // 0-2048 map-texture-pixel values with no extra scaling needed), so
    // applying it here keeps circle size a true, undistorted proportion of
    // the real value instead of a squeezed cosmetic range.
    private const float RadiusToMapPixelDivisor = 7.0f;
    // Floor for the circle's raw diameter (in the same 0-2048 map-pixel
    // space as MarkerCircleRawDiameter's output) so a spot with Radius == 0
    // (only ever seen on placeholder rows, never a real fishing hole - see
    // MarkerCircleRawDiameter) still gets a small, visible marker rather than
    // vanishing entirely.
    private const float MinCircleRawDiameter = 40.0f;
    private const float MinMapZoom = 1.0f;
    // At the fitted zoom of 1 the 2048px map is squeezed to ~0.14 screen
    // pixels per map pixel; 15x takes it past 2 pixels per map pixel, about
    // as far in as the game's own world map goes.
    private const float MaxMapZoom = 15.0f;
    private const float MapZoomStep = 1.25f;
    // When a hole is picked from the list, its range circle is zoomed to fill
    // about this fraction of the map view's shorter side.
    private const float MapCircleFitFraction = 0.7f;
    // Tiny solid-color texture generated at runtime for the map border (see
    // CreateSolidGoldBorderTexture) - a handful of pixels is plenty since it's
    // stretched flat across each border segment, never tiled.
    private const int MapBorderTextureSize = 4;
    // Square texture generated at runtime for each marker's range circle
    // (see CreateAreaCircleTexture) - big enough to stay crisp however large
    // a spot's circle (see MarkerCircleRawDiameter) ends up stretching it.
    private const int MapAreaCircleTextureSize = 128;
    // The game's own icon for the Fishing gathering type (GatheringType sheet,
    // row for Fishing, IconMain field) - the same plain teal fish silhouette
    // the vanilla Fishing Log preview map and the World Map both use for a
    // fishing spot, loaded through Dalamud's icon pipeline (GetFromGameIcon)
    // rather than a custom shipped image, so it always matches whatever the
    // game itself currently draws for fishing spots.
    private const uint FishMapMarkerIconId = 60929;

    // The map panel's layout used to be tunable through Settings -> Debug
    // sliders; those were removed (players never needed to touch them once
    // the layout was right, and they cluttered Settings), but the exact
    // values landed on through that tuning are preserved here as fixed
    // constants rather than resetting everyone back to the untweaked
    // defaults. See DescribeMapState's history for where these numbers came
    // from if they ever need revisiting.
    private const float MapButtonOffsetX = 38.0f;
    private const float MapButtonOffsetY = 7.0f;
    private const float MapDividerOffsetX = -2.0f;
    private const float MapDividerOffsetY = -8.0f;
    private const float MapTitleOffsetX = 2.0f;
    private const float MapTitleOffsetY = -9.0f;
    private const float MapImageOffsetX = 0.0f;
    private const float MapImageOffsetY = 0.0f;
    private const float MapImageWidthOffset = -5.0f;
    private const float MapImageHeightOffset = 105.0f;
    private const float MapBorderThickness = 6.0f;
    private const float MapBorderTopPosition = 3.0f;
    private const float MapBorderBottomPosition = 3.0f;
    private const float MapBorderLeftPosition = 3.0f;
    private const float MapBorderRightPosition = 3.0f;
    private const float MapBorderTopLeftExtend = 0.0f;
    private const float MapBorderTopRightExtend = 0.0f;
    private const float MapBorderBottomLeftExtend = 0.0f;
    private const float MapBorderBottomRightExtend = 0.0f;
    private const float MapBorderLeftTopExtend = 0.0f;
    private const float MapBorderLeftBottomExtend = 0.0f;
    private const float MapBorderRightTopExtend = 0.0f;
    private const float MapBorderRightBottomExtend = 0.0f;

    private ResNode? mapClip;
    private ResNode? mapContent;
    private ImGuiImageNode? mapImage;
    private string? mapLoadedTexturePath;
    private HorizontalLineNode? mapCaptionDivider;
    private LabelTextNode? mapAreaName;
    private LabelTextNode? mapDiscoveredLabel;
    private TextureButtonNode? mapToggleButton;
    // Four thin frame lines around the map clip, in the same warm gold used
    // for the region list. These render a runtime-generated solid-color
    // texture (see CreateSolidGoldBorderTexture) rather than the native
    // divider texture HorizontalLineNode/VerticalLineNode normally draw -
    // that texture's end caps fade out, so a tinted copy of it never came out
    // as a flat, fully opaque color. Replicating the vanilla map window's own
    // frame texture was also considered, but without its real atlas
    // coordinates that risks the same kind of garbled render the map-toggle
    // icon attempt produced earlier.
    private ImGuiImageNode? mapBorderTop;
    private ImGuiImageNode? mapBorderBottom;
    private ImGuiImageNode? mapBorderLeft;
    private ImGuiImageNode? mapBorderRight;
    private JournalArea? mapArea;
    // The area shown as a generic backdrop before any fishing hole has ever
    // been clicked this window (see RefreshMapPreview) - kept separate from
    // mapArea so "nothing genuinely selected yet" stays distinguishable from
    // "this area was actually picked", which is what keeps the map sticky
    // (see ShowAreaMap's own comment) once a real pick happens.
    private JournalArea? previewArea;
    // Keyed by the marker's own fish-icon node (kept as the key, not the
    // spot, so the reference-equality staleness check in LoadMarkerIconAsync
    // keeps working exactly as before) - each entry also carries the range
    // circle drawn behind that same icon, since the two always move and get
    // torn down together (see RebuildMapMarkers/UpdateMarkerLayout).
    private readonly Dictionary<ImGuiImageNode, (JournalSpot Spot, ImGuiImageNode Circle)> mapMarkers = new();
    // One shared, hand-driven tooltip (rather than the native per-node one,
    // which is anchored at the cursor and gets left behind when the map
    // moves): it is re-anchored to the hovered marker's icon every time the
    // map pans or zooms. hoveredMarker is kept through a drag that started on
    // it, so the tooltip follows the marker while the map is dragged.
    private readonly Dictionary<ImGuiImageNode, BackgroundTextNode> markerTooltips = new();
    private readonly HashSet<BackgroundTextNode> measuredTooltips = new();
    // The player's own position/facing marker (the game's blue drop icon,
    // MapPlayerIconId) and the light cone showing which way the camera
    // faces. Both live on the unscaled mapClip layer and are placed by hand
    // (see UpdatePlayerMarker), so they keep their size at any map zoom.
    private const uint MapPlayerIconId = 60443;
    // The drop's own centre hole inside its 32x32 icon texture - the point
    // that sits on the player's position and that the marker rotates around.
    private static readonly Vector2 MapPlayerIconPivot = new(16.5f, 15.5f);
    private const float MapPlayerIconSize = 32.0f;
    // The camera cone sprite is drawn at 96x96 (see LoadPlayerConeAsync). Its
    // rounded back corner sits at about (17.5, 76.5) inside it, and the sprite
    // fans out toward the upper right, so its centre line points 45 degrees
    // clockwise of straight up. The player's point is ~7 pixels forward of
    // that back corner along the centre line (so the cone's rounded back lines
    // up with the drop's rounded back, which sits ~6.5 pixels behind the
    // drop's centre): (17.5, 76.5) + 7 * (sin 45, -cos 45).
    private const int MapPlayerConeSize = 96;
    private static readonly Vector2 MapPlayerConeOrigin = new(22.5f, 71.5f);
    private const float MapPlayerConeBaseAngle = MathF.PI / 4.0f;
    // +1 or -1: flip if the marker/cone turn the wrong way in game.
    private const float MapRotationSign = 1.0f;
    private ImGuiImageNode? playerMarker;
    private ImGuiImageNode? playerCone;
    private uint playerMapTerritory;
    private (float Scale, float OffsetX, float OffsetY)? playerMapInfo;
    private ImGuiImageNode? hoveredMarker;
    private float mapPanX;
    private float mapPanY;
    private float mapZoom = MinMapZoom;
    // True from opening the window until the first area map is shown: that
    // one restores the zoom/pan saved when the journal was last closed (see
    // ShowAreaMap and SaveViewState) instead of centering on the spot.
    private bool restoreMapView;
    private ImGuiImageNode? mapBackdrop;
    private bool draggingMap;
    private float mapDragStartMouseX;
    private float mapDragStartMouseY;
    private float mapDragStartPanX;
    private float mapDragStartPanY;
    private float mapDragDistance;
    // Whether the map panel is currently expanded, separate from the
    // Settings checkbox - the in-window button only flips this, so clicking
    // it to collapse the panel doesn't also hide the button that brings it
    // back. Loaded from configuration.MapPanelOpen in BuildMapPanel so it
    // starts back however the player last left it, instead of always
    // resetting to open.
    private bool mapPanelVisible = true;

    // No longer requires mapArea - the panel starts open (space reserved,
    // border/caption shown) even before any fishing hole has ever been
    // clicked, showing a generic preview map instead of collapsing to
    // nothing (see RefreshMapPreview and ShowAreaMap).
    private bool MapEnabled => !GuideMode && configuration.ShowAreaLocationMap && mapPanelVisible;

    // Includes the map's own height offset below so growing the map shrinks
    // the area list above it to make room, and shrinking the map gives that
    // space back, instead of the map just overlapping its neighbor.
    //
    // MapDividerOffsetY nudges the divider's own drawn position to land it
    // flush against the area list's bottom edge (no visible gap) - that flush
    // look is correct/intended, so this reservation does NOT compensate for
    // it (an earlier attempt to do so only shrank the area list's own visible
    // area further, clipping its last row worse without touching the actual
    // "unclickable near the divider" bug, which lies elsewhere - see
    // areaDividerHandle/regionDividerHandle in LayoutAttachedNodes).
    private float ReservedMapHeight() => MapEnabled
        ? MapDividerHeight + MapCaptionHeight + EffectiveMapHeight() + MapPanelSpacing
        : 0.0f;

    private float EffectiveMapWidth(float areaWidth) => Math.Max(60.0f, areaWidth + MapImageWidthOffset);
    private float EffectiveMapHeight() => Math.Max(60.0f, MapImageHeight + MapImageHeightOffset);

    // The zoomed-out "fit the whole zone map into the preview" scale on its
    // own, with no zoom factored in yet.  Depends on areaWidthSetting, so it
    // changes whenever the area column is resized, not just when the map
    // itself is panned or zoomed.
    private float BaselineMapScale() => EffectiveMapWidth(areaWidthSetting) / 2048.0f;

    // The zoomed-out "fit the whole zone map into the preview" scale, times
    // however far the player has since scrolled in. mapZoom's floor of 1.0
    // means this is always at least that base fit scale.
    private float MapScale() => Math.Max(0.005f, BaselineMapScale() * mapZoom);

    // A spot's range-circle diameter, already expressed in mapContent's own
    // raw 0..2048 map-pixel space (the same space MapPixelPosition uses -
    // see BuildMapInfo's comment) - NOT a screen-pixel size, so no
    // BaselineMapScale conversion is needed here; mapContent's own Scale
    // (see ApplyMapPan) does that for every one of its children uniformly,
    // the same way it already does for position. Dividing the raw sheet
    // Radius by RadiusToMapPixelDivisor (see its own comment) keeps this a
    // true, undistorted proportion of the real per-spot value.
    private static float MarkerCircleRawDiameter(JournalSpot spot)
        => Math.Max(MinCircleRawDiameter, spot.Radius / RadiusToMapPixelDivisor * 2.0f);

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
        // Fills whatever part of the view the map art doesn't cover (panned
        // past the map's edge) with transparent black. Attached before the
        // map content so it sits behind it.
        mapBackdrop = new ImGuiImageNode { FitTexture = true, Alpha = 0.0f };
        mapBackdrop.LoadTexture(CreateBackdropTexture());
        mapBackdrop.TextureSize = new Vector2(MapBorderTextureSize, MapBorderTextureSize);
        mapBackdrop.AttachNode(mapClip);
        mapContent = new ResNode();
        mapContent.AttachNode(mapClip);
        // Transparent until the map texture arrives (LoadMapTextureAsync sets it to 1) -
        // an image node with no texture draws as a black square.
        mapImage = new ImGuiImageNode { Size = new Vector2(2048.0f, 2048.0f), Alpha = 0.0f };
        mapImage.AttachNode(mapContent);

        // Each side gets its own texture instance - ImGuiImageNode takes
        // ownership of whatever texture it's given and disposes it with the
        // node, so the same IDalamudTextureWrap can't be shared across nodes.
        // FitTexture is what actually stretches that tiny texture across the
        // node's full Width/Height (AutoFit + Stretch wrap mode) - without
        // it the image draws at its native few-pixel size instead of filling
        // the line, which is why it first showed up as a row of dots.
        // A layered bronze-and-gold frame instead of one flat line: each
        // side stretches a small strip texture whose pixels run dark bronze ->
        // gold -> bright gold -> gold -> dark bronze from the outer edge in.
        mapBorderTop = new ImGuiImageNode { FitTexture = true };
        mapBorderTop.LoadTexture(CreateBronzeBorderTexture(vertical: false, outerFirst: true));
        mapBorderTop.TextureSize = new Vector2(MapBorderPatternSize, MapBorderPatternSize);
        mapBorderTop.AttachNode(this);
        mapBorderBottom = new ImGuiImageNode { FitTexture = true };
        mapBorderBottom.LoadTexture(CreateBronzeBorderTexture(vertical: false, outerFirst: false));
        mapBorderBottom.TextureSize = new Vector2(MapBorderPatternSize, MapBorderPatternSize);
        mapBorderBottom.AttachNode(this);
        mapBorderLeft = new ImGuiImageNode { FitTexture = true };
        mapBorderLeft.LoadTexture(CreateBronzeBorderTexture(vertical: true, outerFirst: true));
        mapBorderLeft.TextureSize = new Vector2(MapBorderPatternSize, MapBorderPatternSize);
        mapBorderLeft.AttachNode(this);
        mapBorderRight = new ImGuiImageNode { FitTexture = true };
        mapBorderRight.LoadTexture(CreateBronzeBorderTexture(vertical: true, outerFirst: false));
        mapBorderRight.TextureSize = new Vector2(MapBorderPatternSize, MapBorderPatternSize);
        mapBorderRight.AttachNode(this);

        // A plain text button - the fish icon shipped with the plugin is
        // used for the map markers themselves (see RebuildMapMarkers), not
        // this button. Its actual label ("Show Map"/"Hide Map") is kept in
        // sync with mapPanelVisible from LayoutMapPanel.
        // An icon button (the game's "map window" toolbar glyph) in the footer
        // row beside the normal-log and search buttons; its tooltip says whether
        // it will show or hide the map (see LayoutMapPanel).
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

    // A flat-filled solid-gold texture, generated on the fly rather than
    // loaded from a game or disk file, for the map border lines. The native
    // divider texture those used to draw with (ui/uld/WindowA_Line.tex) has a
    // faded/tapered end cap baked into its pixels, and no amount of Color
    // tinting can turn that into a uniform, fully opaque color - only the
    // pixels themselves can. Every pixel here is the same warm gold used for
    // the region list, at full alpha, so the border reads as one solid color
    // with no transparency however far it's stretched.
    private static IDalamudTextureWrap CreateSolidGoldBorderTexture()
    {
        var specification = RawImageSpecification.Rgba32(MapBorderTextureSize, MapBorderTextureSize);
        Span<byte> pixels = stackalloc byte[MapBorderTextureSize * MapBorderTextureSize * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = (byte)(RegionListTextColor.X * 255.0f); // R
            pixels[i + 1] = (byte)(RegionListTextColor.Y * 255.0f); // G
            pixels[i + 2] = (byte)(RegionListTextColor.Z * 255.0f); // B
            pixels[i + 3] = 255; // A - always fully opaque
        }
        return Services.TextureProvider.CreateFromRaw(specification, pixels, "FishingCluesMapBorder");
    }

    private const int MapBorderPatternSize = 6;
    private static readonly (byte R, byte G, byte B)[] BronzeBorderRamp =
    {
        // Layered around #977645 (151, 118, 69): a darker outer edge, the
        // base color, a lighter highlight in the middle, the base color again,
        // and a darker inner edge.
        (85, 64, 38), (151, 118, 69), (196, 160, 105), (222, 188, 132), (151, 118, 69), (85, 64, 38),
    };

    // One side of the border: MapBorderPatternSize pixels thick, colored from
    // BronzeBorderRamp across that thickness (outer edge first, or last for
    // the bottom/right sides), and constant along the line's length.
    private static IDalamudTextureWrap CreateBronzeBorderTexture(bool vertical, bool outerFirst)
    {
        const int size = MapBorderPatternSize;
        var specification = RawImageSpecification.Rgba32(size, size);
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int across = vertical ? x : y;
                var (r, g, b) = BronzeBorderRamp[outerFirst ? across : size - 1 - across];
                int i = (y * size + x) * 4;
                pixels[i + 0] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }
        return Services.TextureProvider.CreateFromRaw(specification, pixels, "FishingCluesMapBorder");
    }

    // Black at ~60% opacity - transparent enough for the window behind to
    // show through, dark enough that the map's edge reads clearly.
    private static IDalamudTextureWrap CreateBackdropTexture()
    {
        var specification = RawImageSpecification.Rgba32(MapBorderTextureSize, MapBorderTextureSize);
        var pixels = new byte[MapBorderTextureSize * MapBorderTextureSize * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 153;
        return Services.TextureProvider.CreateFromRaw(specification, pixels, "FishingCluesMapBackdrop");
    }

    // A hole picked while the map was hidden, applied when it is shown again.
    private bool hasPendingMapFocus;
    private JournalSpot? pendingMapFocusSpot;
    private bool pendingMapZoomToSpot;
    private bool pendingMapAreaChanged;

    private void ToggleMapVisibility()
    {
        mapPanelVisible = !mapPanelVisible;
        configuration.MapPanelOpen = mapPanelVisible;
        saveDivider();
        // ShowAreaMap/RefreshMapPreview track the caption/discovered-count
        // even while collapsed, but skip the actual texture/marker work in
        // that state - catch it back up now that the panel is visible again,
        // rather than leaving it blank until the player happens to click a
        // different hole or browse to a different region. Opening the map
        // (whether via this toggle or by first showing the journal) always
        // starts from the whole-area overview - resetting the zoom here
        // means it's never left showing wherever a previously clicked hole
        // happened to zoom in to.
        // The zoom and pan are kept across hiding and showing the map (the
        // fields are untouched while it's hidden), so it comes back exactly
        // as it was left.
        if (mapPanelVisible)
        {
            if (mapArea is not null && hasPendingMapFocus)
            {
                // A hole was picked while the map was hidden: show it now exactly
                // as if the map had been open when it was picked.
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

    // Shown as a generic backdrop before any fishing hole has ever been
    // clicked in this window - keeps the panel from starting out empty (see
    // MapEnabled no longer requiring a real selection) without pretending a
    // hole has actually been picked (mapArea itself is left null, so
    // ShowAreaMap's own "sticky until a real pick" behavior is unaffected).
    // Called on first open and every time SelectRegion switches regions,
    // for as long as mapArea stays null - once a hole is genuinely clicked,
    // ShowAreaMap takes over and this is never consulted again. The area
    // parameter is currently unused for the texture itself (the backdrop is
    // always the whole-world overview, see WorldMapTexturePath), but is
    // still tracked so a future per-region backdrop wouldn't need callers
    // to change.
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

    // The whole-continent overview (Map sheet row 1, "Eorzea") shown as the
    // generic backdrop before any fishing hole has been picked - the same
    // top-level image the real in-game World Map shows before drilling into
    // a specific region. FFXIV has one such row per continent, not a single
    // image spanning all of them; this always uses the Eorzea one, since
    // nearly every fishing region lives on that continent, rather than
    // trying to resolve the "right" continent's overview per region. Built
    // from the sheet the same way JournalBuilder.BuildMapInfo derives every
    // other map's texture path from its own Id field, rather than a
    // hardcoded path, so it keeps working if that row's Id ever changes.
    private const uint WorldMapRowId = 1;

    private static string? WorldMapTexturePath()
    {
        if (!Services.DataManager.GetExcelSheet<MapSheet>().TryGetRow(WorldMapRowId, out MapSheet mapRow))
            return null;
        string id = mapRow.Id.ToString();
        int slash = id.IndexOf('/');
        return slash > 0 && slash < id.Length - 1
            ? $"ui/map/{id[..slash]}/{id[(slash + 1)..]}/{id[..slash]}{id[(slash + 1)..]}_m.tex"
            : null;
    }

    // Refreshes the map for the given area. Only called when a fishing hole
    // is actually selected (from the area list or a marker click) - NOT when
    // an area header is merely expanded/collapsed, and NOT just from
    // switching regions/areas with nothing yet clicked in them, so browsing
    // around never yanks the map away from whatever hole is currently
    // shown. When focusSpot is given (the hole that was just clicked) the
    // map centers on it specifically instead of the area's default hole.
    private void ShowAreaMap(JournalArea area, JournalSpot? focusSpot = null, bool zoomToSpot = false, bool preserveView = false)
    {
        bool areaChanged = !ReferenceEquals(mapArea, area);
        mapArea = area;
        if (mapAreaName is not null) mapAreaName.String = area.Name;
        int discovered = area.Spots.Count(s => s.IsUnlocked);
        if (mapDiscoveredLabel is not null)
            mapDiscoveredLabel.String = $"Locations Discovered: {discovered}/{area.Spots.Count}";
        if (!MapEnabled)
        {
            // Hidden: remember what was asked for so the map, when shown again,
            // looks as if it had been updated all along (see ToggleMapVisibility).
            hasPendingMapFocus = true;
            pendingMapFocusSpot = focusSpot;
            pendingMapZoomToSpot = zoomToSpot;
            pendingMapAreaChanged |= areaChanged;
            return;
        }

        // A new area starts back at the zoomed-out fit view - carrying over
        // whatever zoom/pan the player left the previous area's map at would
        // be disorienting (and could show empty space for a smaller zone).
        if (areaChanged) mapZoom = MinMapZoom;

        var withMap = area.Spots.Where(s => s.MapPixelPosition is not null).ToArray();
        // Only discovered holes get a pin - an undiscovered one's marker
        // would give away exactly where it sits before the player has found
        // it themselves, which is the same spoiler the map-hiding idea from
        // earlier was trying to avoid, just scoped to the pin instead of the
        // whole map (the whole-map hide isn't needed: this window only ever
        // shows an area after a discovered hole in it was actually clicked).
        var discoveredWithMap = withMap.Where(s => s.IsUnlocked).ToArray();
        // Diagnostic for the "fishing holes don't show" reports: pins down
        // whether the data even reaches here (a spot count with no map
        // position means the game-data lookup in JournalBuilder is the
        // problem, not the marker rendering) versus a rendering-side issue.
        Services.Log.Debug($"[FishingClues] Area map: '{area.Name}' has {area.Spots.Count} spot(s), "
            + $"{withMap.Length} with a map position ({discoveredWithMap.Length} discovered); texture='{withMap.FirstOrDefault()?.MapTexturePath}'");
        EnsureMapTexture(withMap.FirstOrDefault()?.MapTexturePath);
        RebuildMapMarkers(discoveredWithMap);
        // When nothing specific was clicked, center on the whole cluster of
        // this area's own discovered holes (their bounding box's own
        // center), not just whichever one happens to be first in the list.
        // A real zone's meaningful map content is rarely centered on the
        // texture's own (1024,1024) middle, and isn't necessarily centered
        // on any ONE hole's position either - e.g. New Gridania's holes both
        // sit toward the top of their texture, so centering on just the
        // first one (whichever that happened to be) left the view pushed up
        // against the drawn content's own edge, showing a chunk of the
        // texture's unused/blank margin below it instead of the map looking
        // centered the way the vanilla journal's own preview does.
        Vector2 defaultFocus = AreaFocusPixel(discoveredWithMap) ?? new Vector2(1024.0f, 1024.0f);
        bool restoredView = false;
        if (restoreMapView)
        {
            // Only the first area map shown after the window opens gets the
            // saved view, and only if it is the same area it was saved for.
            restoreMapView = false;
            if (sessionState.MapArea == area.Name && sessionState.MapZoom > 0.0f)
            {
                mapZoom = Math.Clamp(sessionState.MapZoom, MinMapZoom, MaxMapZoom);
                mapPanX = sessionState.MapPanX;
                mapPanY = sessionState.MapPanY;
                ClampMapPan();
                restoredView = true;
                lastCenteredFocus = null; // the saved view wins over any centering
            }
        }
        if (!restoredView && preserveView)
        {
            // Re-showing the same map: keep the current zoom/pan, only
            // re-clamping it in case the panel's size changed meanwhile.
            ClampMapPan();
        }
        else if (!restoredView)
        {
            // Picking a hole from the fish-spot list zooms to fit that hole's
            // whole range circle in view (zooming in for a small circle, out
            // for a big one) instead of always zooming all the way in.
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

    // The center of the smallest box containing every given spot's map
    // pixel position - used as the default view focus for an area's map
    // (see ShowAreaMap) instead of any single spot's own position, so a
    // multi-hole area centers on the middle of the whole cluster rather
    // than wherever its first hole happens to sit. Null only when none of
    // the spots have a usable map position at all (ShowAreaMap's own
    // (1024,1024) texture-center fallback covers that case).
    private static Vector2? AreaFocusPixel(IReadOnlyList<JournalSpot> spots)
    {
        float? minX = null, maxX = null, minY = null, maxY = null;
        foreach (JournalSpot spot in spots)
        {
            if (spot.MapPixelPosition is not Vector2 pixel) continue;
            minX = minX is float mx ? Math.Min(mx, pixel.X) : pixel.X;
            maxX = maxX is float ax ? Math.Max(ax, pixel.X) : pixel.X;
            minY = minY is float my ? Math.Min(my, pixel.Y) : pixel.Y;
            maxY = maxY is float ay ? Math.Max(ay, pixel.Y) : pixel.Y;
        }
        if (minX is not float left || maxX is not float right || minY is not float top || maxY is not float bottom)
            return null;
        return new Vector2((left + right) / 2.0f, (top + bottom) / 2.0f);
    }

    // Feeds the "fishing holes don't show" diagnostic into the same
    // in-game diagnostic report the Settings -> Debug button already
    // produces (see Plugin.LogJournalDiagnostics), rather than a /xllog
    // line the player has to go dig up separately - this is the report
    // players already know how to copy and paste back over.
    public string DescribeMapState()
    {
        var report = new System.Text.StringBuilder();

        if (GuideMode || mapArea is null)
        {
            report.AppendLine("Area map: no area currently shown in this journal window.");
            return report.ToString();
        }

        int totalSpots = mapArea.Spots.Count;
        var withMap = mapArea.Spots.Where(s => s.MapPixelPosition is not null).ToArray();
        report.AppendLine($"Area map: area='{mapArea.Name}', {totalSpots} spot(s), {withMap.Length} with a map position");
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

    // Loaded through Dalamud's own ITextureProvider rather than the raw
    // native ATK asset loader (the same path every other KamiToolKit node
    // uses, and the one that silently produced a blank map). ITextureProvider
    // is the same well-tested pipeline every minimap/teleport-preview plugin
    // uses to show map thumbnails, so it's a much safer bet for a full
    // 2048x2048 zone map than the native loader turned out to be - it also
    // handles the "_hr1" high-resolution variant itself, so this always
    // requests the plain base path.
    private void EnsureMapTexture(string? texturePath)
    {
        if (mapImage is null || string.IsNullOrEmpty(texturePath) || texturePath == mapLoadedTexturePath)
            return;
        mapLoadedTexturePath = texturePath;
        Services.Log.Debug($"[FishingClues] Area map: requesting '{texturePath}' (FileExists={Services.DataManager.FileExists(texturePath)})");
        _ = LoadMapTextureAsync(texturePath);
    }

    // The game's own world map is two textures per map: "<id>m_m.tex" is the
    // parchment sheet (paper grain, border and the zone-name banner) and
    // "<id>_m.tex" is the terrain art, drawn over it multiplied. Showing
    // "_m" alone (what this used to do) leaves out the paper, frame and
    // banner and looks washed out. The two are multiplied together once
    // here, off the game thread, into a single texture. Returns null (the
    // caller falls back to plain "_m") when the paper layer doesn't exist or
    // doesn't match in size, e.g. for a map that has no paper layer.
    private static async System.Threading.Tasks.Task<IDalamudTextureWrap?> CreateMapCompositeAsync(string texturePath)
    {
        const string suffix = "_m.tex";
        if (!texturePath.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string paperPath = texturePath[..^suffix.Length] + "m_m.tex";
        if (!Services.DataManager.FileExists(paperPath)) return null;

        (int Width, int Height, byte[] Pixels)? composite = await System.Threading.Tasks.Task.Run(() =>
        {
            var map = Services.DataManager.GetFile<Lumina.Data.Files.TexFile>(texturePath);
            var paper = Services.DataManager.GetFile<Lumina.Data.Files.TexFile>(paperPath);
            if (map is null || paper is null) return ((int, int, byte[])?)null;
            int width = map.Header.Width, height = map.Header.Height;
            if (paper.Header.Width != width || paper.Header.Height != height) return null;
            byte[] top = map.ImageData, bottom = paper.ImageData;
            var pixels = new byte[width * height * 4];
            // Lumina's decoded image data is BGRA; the output is RGBA.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 0] = (byte)(top[i + 2] * bottom[i + 2] / 255);
                pixels[i + 1] = (byte)(top[i + 1] * bottom[i + 1] / 255);
                pixels[i + 2] = (byte)(top[i + 0] * bottom[i + 0] / 255);
                pixels[i + 3] = 255;
            }
            return (width, height, pixels);
        });
        if (composite is not var (w, h, data)) return null;
        return await Services.TextureProvider.CreateFromRawAsync(RawImageSpecification.Rgba32(w, h), data, "FishingCluesMapComposite");
    }

    // Built map textures (the parchment/terrain composite is slow to make), kept
    // across window openings so the map appears at once when the journal reopens.
    // Each window gets its own wrap sharing the cached one's GPU resource, since a
    // node disposes the wrap it is given. At most MapTextureCacheSize maps are kept.
    private const int MapTextureCacheSize = 4;
    private static readonly Dictionary<string, System.Threading.Tasks.Task<IDalamudTextureWrap?>> MapTextureCache = new();
    private static readonly List<string> MapTextureCacheOrder = new();

    private static System.Threading.Tasks.Task<IDalamudTextureWrap?> GetCachedMapTextureAsync(string path)
    {
        lock (MapTextureCache)
        {
            if (MapTextureCache.TryGetValue(path, out var existing)) return existing;
            var task = BuildMapTextureAsync(path);
            MapTextureCache[path] = task;
            MapTextureCacheOrder.Add(path);
            while (MapTextureCacheOrder.Count > MapTextureCacheSize)
            {
                string oldest = MapTextureCacheOrder[0];
                MapTextureCacheOrder.RemoveAt(0);
                if (MapTextureCache.Remove(oldest, out var evicted))
                    _ = evicted.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); });
            }
            return task;
        }
    }

    private static async System.Threading.Tasks.Task<IDalamudTextureWrap?> BuildMapTextureAsync(string path)
    {
        try
        {
            return await CreateMapCompositeAsync(path) ?? await Services.TextureProvider.GetFromGame(path).RentAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"[FishingClues] Area map: failed to load '{path}'");
            lock (MapTextureCache) { MapTextureCache.Remove(path); MapTextureCacheOrder.Remove(path); }
            return null;
        }
    }

    // Starts building the map for a territory in the background (a no-op if it's
    // already built or building), so the journal's map is ready when it opens.
    public static void PrewarmMapCache(uint territoryId)
    {
        if (territoryId == 0) return;
        if (!Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(territoryId, out var territory)
            || territory.Map.ValueNullable is not { } map || map.RowId == 0) return;
        string id = map.Id.ToString();
        int slash = id.IndexOf('/');
        if (slash <= 0 || slash >= id.Length - 1) return;
        _ = GetCachedMapTextureAsync($"ui/map/{id[..slash]}/{id[(slash + 1)..]}/{id[..slash]}{id[(slash + 1)..]}_m.tex");
    }

    public static void ClearMapTextureCache()
    {
        lock (MapTextureCache)
        {
            foreach (var task in MapTextureCache.Values)
                _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); });
            MapTextureCache.Clear();
            MapTextureCacheOrder.Clear();
        }
    }

    private async System.Threading.Tasks.Task LoadMapTextureAsync(string texturePath)
    {
        IDalamudTextureWrap texture;
        try
        {
            var cached = await GetCachedMapTextureAsync(texturePath);
            if (cached is null) return;
            texture = cached.CreateWrapSharingLowLevelResource();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"[FishingClues] Area map: failed to load '{texturePath}'");
            return;
        }

        await Services.Framework.Run(() =>
        {
            // The player may have navigated to a different area (or closed
            // the window) while this was in flight - only apply it if it's
            // still the map path currently wanted, otherwise just dispose it.
            if (mapImage is null || mapLoadedTexturePath != texturePath)
            {
                texture.Dispose();
                return;
            }
            Services.Log.Debug($"[FishingClues] Area map: loaded '{texturePath}' at {texture.Size}");
            mapImage.LoadTexture(texture);
            mapImage.TextureSize = new Vector2(2048.0f, 2048.0f);
            mapImage.Alpha = 1.0f;
            // The dark backdrop only appears together with the map, so opening the
            // journal never shows the translucent black on its own while it loads.
            if (mapBackdrop is not null) mapBackdrop.Alpha = 1.0f;
        });
    }

    // The fish icon and its range circle are deliberately parented under two
    // DIFFERENT layers now, because they're meant to behave differently as
    // the map zooms:
    //
    // - The icon is attached to mapClip (the un-scaled clip window) and has
    //   its screen position recomputed by hand on every pan/zoom (see
    //   UpdateMarkerLayout) - it needs to stay a fixed, reliably clickable
    //   screen size at any zoom level, the same way a pin on a real map
    //   doesn't shrink to nothing when you zoom out.
    // - The circle is attached to mapContent (the scaled/panned map layer)
    //   with its size/position expressed in mapContent's own raw (0..2048)
    //   space (see MarkerCircleRawDiameter), so mapContent's existing
    //   Scale/Position carries it along like a decal painted on the map
    //   itself - it grows and shrinks with zoom, and its size varies per
    //   spot from FishingSpot.Radius (see MarkerCircleRawDiameter), matching
    //   the vanilla journal's own differently-sized range circles.
    //   An earlier attempt at parenting the icon here too and counter-
    //   scaling it to stay screen-fixed needed the node to hold a very large
    //   Scale value (10-30x at the zoomed-out baseline) that never rendered
    //   reliably - keeping the icon on the unscaled layer sidesteps that
    //   entirely.
    //
    // Drawn with the game's own Fishing gathering-type icon (see
    // FishMapMarkerIconId) on top of a translucent range circle (see
    // CreateAreaCircleTexture), matching the vanilla Fishing Log preview map
    // and World Map's own look for a fishing spot. Each marker's circle and
    // icon get their own rented/generated texture instance - ImGuiImageNode.
    // LoadTexture takes ownership and disposes it with the node, so the same
    // texture can't be shared across markers (the same reason the border
    // lines each load their own copy of their texture).
    private void RebuildMapMarkers(IReadOnlyList<JournalSpot> spots)
    {
        if (mapClip is null || mapContent is null) return;
        foreach (var (icon, data) in mapMarkers)
        {
            icon.Dispose();
            data.Circle.Dispose();
        }
        mapMarkers.Clear();
        DisposeMarkerTooltips();
        // Callers only ever pass already-discovered spots (see ShowAreaMap) -
        // an undiscovered hole never gets a pin, since that would give away
        // exactly where it sits before the player has found it themselves.
        foreach (JournalSpot spot in spots)
        {
            if (spot.MapPixelPosition is null) continue;
            // The circle's actual Size/Position get set for real in
            // UpdateMarkerLayout (called below) since they depend on the
            // spot's own Radius - attached first so it renders behind the
            // fish icon.
            var circle = new ImGuiImageNode { FitTexture = true };
            circle.AttachNode(mapContent);
            circle.LoadTexture(CreateAreaCircleTexture());
            circle.TextureSize = new Vector2(MapAreaCircleTextureSize, MapAreaCircleTextureSize);
            var icon = new ImGuiImageNode
            {
                Size = new Vector2(MapMarkerSize, MapMarkerSize),
                FitTexture = true,
                // Stays transparent until its texture arrives (see
                // LoadMarkerIconAsync) - an empty image node draws as a black
                // square.
                Alpha = 0.0f,
            };
            icon.AttachNode(mapClip);
            mapMarkers.Add(icon, (spot, circle));
            _ = LoadMarkerIconAsync(icon);
        }
        CreatePlayerMarker();
        // Created after the icons so they are drawn on top of them.
        foreach (var (icon, data) in mapMarkers)
        {
            var tooltip = new BackgroundTextNode
            {
                FontType = FontType.Axis,
                FontSize = 14,
                Size = new Vector2(200.0f, 24.0f),
                String = data.Spot.Name,
                IsVisible = false,
                Alpha = 0.0f,
                Position = new Vector2(-10000.0f, -10000.0f),
            };
            tooltip.AttachNode(mapClip);
            markerTooltips.Add(icon, tooltip);
        }
        Services.Log.Debug($"[FishingClues] Area map: placed {mapMarkers.Count} marker(s) for '{mapArea?.Name}'");
        UpdateMarkerLayout();
    }

    private async System.Threading.Tasks.Task LoadMarkerIconAsync(ImGuiImageNode icon)
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
            // This exact marker may have been torn down (a different hole
            // was clicked, or the window closed) while the load was in
            // flight - mapMarkers.Clear() in RebuildMapMarkers/DisposeMapPanel
            // is what would have removed it, so checking membership here
            // (rather than a field null-check, since there's no single
            // "the current icon" field to compare against) is the
            // equivalent staleness check.
            if (!mapMarkers.ContainsKey(icon))
            {
                texture.Dispose();
                return;
            }
            icon.LoadTexture(texture);
            icon.Alpha = 1.0f;
        });
    }

    // A single plain, flat translucent teal circle - generated at runtime the
    // same way CreateSolidGoldBorderTexture builds the map's border lines,
    // rather than shipping a fixed-size image, so it stays crisp at whatever
    // diameter MarkerCircleRawDiameter lands on for a given spot. An earlier
    // version added a dashed ring around the edge to mimic the vanilla map's
    // dotted boundary more closely, but with several markers close together
    // on the same zone that read as a chaotic overlapping mess rather than
    // distinct circles - a plain flat fill is easier to tell apart at a
    // glance.
    private static IDalamudTextureWrap CreateAreaCircleTexture()
    {
        const int size = MapAreaCircleTextureSize;
        const float center = size / 2.0f;
        const float radius = size / 2.0f - 2.0f;

        var specification = RawImageSpecification.Rgba32(size, size);
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                float dx = x + 0.5f - center;
                float dy = y + 0.5f - center;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                pixels[i + 0] = 130; // R
                pixels[i + 1] = 220; // G
                pixels[i + 2] = 215; // B
                pixels[i + 3] = dist <= radius ? (byte)70 : (byte)0; // A
            }
        }
        return Services.TextureProvider.CreateFromRaw(specification, pixels, "FishingCluesMapAreaCircle");
    }

    // The current viewport's extent expressed in mapContent's own raw
    // (0..2048) space - mapPanX/mapPanY are already that space's top-left
    // corner, so this just adds how wide/tall the view is at the current
    // zoom. Shared by the circle-visibility culling below (the icon's own
    // visibility is computed directly in its own, unscaled mapClip space
    // instead, since it lives on a different layer - see UpdateMarkerLayout).
    private (float Left, float Top, float Width, float Height) CurrentMapView()
    {
        float scale = MapScale();
        float clipWidth = mapClip?.Width ?? EffectiveMapWidth(areaWidthSetting);
        float clipHeight = mapClip?.Height ?? EffectiveMapHeight();
        return (mapPanX, mapPanY, clipWidth / scale, clipHeight / scale);
    }

    // Recomputes every marker's screen position (icon) and raw size/position
    // (circle) - called whenever the pan, zoom, or area column width changes
    // (both a marker's screen position and a circle's raw size depend on
    // one of those), and once after RebuildMapMarkers creates them.
    // Visibility is tracked by hand for both rather than left to mapClip's
    // own native clip: that clip hides rendering but not click hit-testing,
    // so a marker panned outside the visible area would stay "clickable" at
    // its off-screen position without this (see TryClickMarker's IsVisible
    // check).
    private void UpdateMarkerLayout()
    {
        if (mapClip is null || mapContent is null) return;
        float scale = MapScale();
        Vector2 pan = new(mapPanX, mapPanY);
        Vector2 iconHalf = new(MapMarkerSize / 2.0f, MapMarkerSize / 2.0f);
        float clipWidth = mapClip.Width;
        float clipHeight = mapClip.Height;
        (float viewLeft, float viewTop, float viewWidth, float viewHeight) = CurrentMapView();
        foreach (var (icon, data) in mapMarkers)
        {
            JournalSpot spot = data.Spot;
            if (spot.MapPixelPosition is not Vector2 pixel)
            {
                icon.IsVisible = false;
                data.Circle.IsVisible = false;
                continue;
            }
            // Icon: fixed screen size, tracked by hand in mapClip's own
            // unscaled space so it never resizes with zoom (see
            // MapMarkerSize's own comment).
            Vector2 iconLocal = (pixel - pan) * scale - iconHalf;
            icon.Position = iconLocal;
            icon.IsVisible = iconLocal.X + MapMarkerSize >= 0.0f && iconLocal.Y + MapMarkerSize >= 0.0f
                && iconLocal.X <= clipWidth && iconLocal.Y <= clipHeight;

            // Circle: sized from the spot's own real range (see
            // MarkerCircleRawDiameter), parented under mapContent so it
            // scales with zoom like a decal painted on the map, unlike the
            // icon.
            float circleRaw = MarkerCircleRawDiameter(spot);
            Vector2 circleHalf = new(circleRaw / 2.0f, circleRaw / 2.0f);
            data.Circle.Size = new Vector2(circleRaw, circleRaw);
            data.Circle.Position = pixel - circleHalf;
            bool circleVisible = pixel.X + circleRaw / 2.0f >= viewLeft && pixel.X - circleRaw / 2.0f <= viewLeft + viewWidth
                && pixel.Y + circleRaw / 2.0f >= viewTop && pixel.Y - circleRaw / 2.0f <= viewTop + viewHeight;
            data.Circle.IsVisible = circleVisible;
            if (configuration.DebugMode)
            {
                Services.Log.Debug($"[FishingClues] Area map: marker '{spot.Name}' pixel={pixel} iconLocal={iconLocal} "
                    + $"radius={spot.Radius} circleRawDiameter={circleRaw:0.#} onScreenDiameter={circleRaw * scale:0.#} "
                    + $"clip={clipWidth}x{clipHeight} scale={scale} iconVisible={icon.IsVisible} circleVisible={circleVisible}");
            }
        }
        // Same pass as the icons, so the player marker moves in the very
        // frame the map pans instead of trailing it by one.
        unsafe { UpdatePlayerMarker(); }
        RefreshMarkerTooltip();
    }

    // Re-anchors the shared tooltip to the hovered marker's icon (centered
    // above it, or below it when there's no room above, and kept inside the
    // map view). Called on every hover change and from UpdateMarkerLayout, so
    // it also follows the marker while the map is panned or zoomed.
    private void RefreshMarkerTooltip()
    {
        if (mapClip is null) return;
        // Every marker owns its own pre-rendered tooltip node (created in
        // RebuildMapMarkers), so hovering another marker only hides one node
        // and shows another - swapping one shared node's text on screen
        // flashed the previous name for a frame, because the native text
        // node re-renders a changed string a frame late.
        foreach (var (icon, tip) in markerTooltips)
            if (!ReferenceEquals(icon, hoveredMarker) && (tip.IsVisible || tip.Alpha > 0.0f))
            {
                // Hidden three ways at once (invisible, transparent and moved
                // far off the map) so the previous tooltip can't linger for a
                // frame if the game applies one of them late.
                tip.IsVisible = false;
                tip.Alpha = 0.0f;
                tip.Position = new Vector2(-10000.0f, -10000.0f);
            }
        if (hoveredMarker is null || !hoveredMarker.IsVisible || !markerTooltips.TryGetValue(hoveredMarker, out var tooltip))
            return;
        // Sized in the background while hidden (MeasurePendingTooltips) so
        // that resizing never happens on the frame it first appears.
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

    // Called every frame: gives each still-hidden tooltip its final size once
    // its text can be measured, a few frames after it was created.
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

    private void CreatePlayerMarker()
    {
        if (mapClip is null) return;
        // Cone first so the drop draws on top of it.
        playerCone = new ImGuiImageNode
        {
            Size = new Vector2(MapPlayerConeSize, MapPlayerConeSize),
            FitTexture = true,
            Origin = MapPlayerConeOrigin,
            // Transparent until the sprite arrives (an empty image node draws
            // as a black square).
            Alpha = 0.0f,
            IsVisible = false,
        };
        playerCone.AttachNode(mapClip);
        _ = LoadPlayerConeAsync(playerCone);
        playerMarker = new ImGuiImageNode
        {
            Size = new Vector2(MapPlayerIconSize, MapPlayerIconSize),
            FitTexture = true,
            Origin = MapPlayerIconPivot,
            // Transparent until its texture arrives (an empty image node
            // draws as a black square).
            Alpha = 0.0f,
            IsVisible = false,
        };
        playerMarker.AttachNode(mapClip);
        _ = LoadPlayerMarkerIconAsync(playerMarker);
    }

    private async System.Threading.Tasks.Task LoadPlayerMarkerIconAsync(ImGuiImageNode node)
    {
        IDalamudTextureWrap texture;
        try
        {
            texture = await Services.TextureProvider.GetFromGameIcon(MapPlayerIconId).RentAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "[FishingClues] Area map: failed to load the player marker icon.");
            return;
        }
        await Services.Framework.Run(() =>
        {
            if (!ReferenceEquals(node, playerMarker))
            {
                texture.Dispose();
                return;
            }
            node.LoadTexture(texture);
            node.Alpha = 1.0f;
        });
    }

    // The game's own camera cone: the 96x96 sprite at (352,0) of
    // ui/uld/NaviMap.tex, i.e. the 192x192 block at (704,0) of its high-res
    // sheet NaviMap_hr1.tex (read straight from the game data and cropped
    // here). It is a soft fan glow whose bright corner sits at the player.
    private const string MapConeSheetPath = "ui/uld/NaviMap_hr1.tex";
    private const int MapConeSheetX = 704;
    private const int MapConeSheetSize = 192;

    private async System.Threading.Tasks.Task LoadPlayerConeAsync(ImGuiImageNode node)
    {
        IDalamudTextureWrap texture;
        try
        {
            var pixels = await System.Threading.Tasks.Task.Run(() =>
            {
                var sheet = Services.DataManager.GetFile<Lumina.Data.Files.TexFile>(MapConeSheetPath);
                if (sheet is null) return null;
                int width = sheet.Header.Width;
                byte[] source = sheet.ImageData;
                var crop = new byte[MapConeSheetSize * MapConeSheetSize * 4];
                for (int y = 0; y < MapConeSheetSize; y++)
                {
                    for (int x = 0; x < MapConeSheetSize; x++)
                    {
                        int from = (y * width + MapConeSheetX + x) * 4;
                        int to = (y * MapConeSheetSize + x) * 4;
                        // Lumina's image data is BGRA; the texture is RGBA.
                        crop[to + 0] = source[from + 2];
                        crop[to + 1] = source[from + 1];
                        crop[to + 2] = source[from + 0];
                        crop[to + 3] = source[from + 3];
                    }
                }
                return crop;
            });
            if (pixels is null) return;
            texture = await Services.TextureProvider.CreateFromRawAsync(
                RawImageSpecification.Rgba32(MapConeSheetSize, MapConeSheetSize), pixels, "FishingCluesMapPlayerCone");
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "[FishingClues] Area map: failed to load the camera cone sprite.");
            return;
        }
        await Services.Framework.Run(() =>
        {
            if (!ReferenceEquals(node, playerCone))
            {
                texture.Dispose();
                return;
            }
            node.LoadTexture(texture);
            node.TextureSize = new Vector2(MapConeSheetSize, MapConeSheetSize);
            node.Alpha = 1.0f;
        });
    }

    // Places the player marker and camera cone on the map: the player's world
    // position converted to the map texture's pixel space with the map's own
    // SizeFactor/Offset, then to a screen position in mapClip's unscaled
    // space (so neither shrinks when the map zooms out). Shown only while
    // the player is standing in the territory whose map is displayed.
    private unsafe void UpdatePlayerMarker()
    {
        if (playerMarker is null || playerCone is null || mapClip is null) return;
        var player = Services.ObjectTable.LocalPlayer;
        uint territory = Services.ClientState.TerritoryType;
        if (player is null || mapArea is null || mapArea.Spots.Count == 0 || mapArea.Spots[0].TerritoryId != territory)
        {
            playerMarker.IsVisible = playerCone.IsVisible = false;
            return;
        }
        if (ResolvePlayerMapInfo(territory) is not var (mapScale, offsetX, offsetY))
        {
            playerMarker.IsVisible = playerCone.IsVisible = false;
            return;
        }
        Vector2 pixel = new((player.Position.X + offsetX) * mapScale + 1024.0f, (player.Position.Z + offsetY) * mapScale + 1024.0f);
        Vector2 local = (pixel - new Vector2(mapPanX, mapPanY)) * MapScale();
        bool visible = local.X >= -MapPlayerConeSize && local.Y >= -MapPlayerConeSize
            && local.X <= mapClip.Width + MapPlayerConeSize && local.Y <= mapClip.Height + MapPlayerConeSize;
        playerMarker.IsVisible = playerCone.IsVisible = visible;
        if (!visible) return;

        // Facing angle (radians) -> clockwise node rotation with the icon/cone
        // drawn pointing up (north): the game's heading 0 faces +Z (south).
        playerMarker.Position = local - MapPlayerIconPivot;
        playerMarker.Rotation = MapRotationSign * (MathF.PI - player.Rotation);
        playerCone.Position = local - MapPlayerConeOrigin;
        var camera = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        var active = camera is null ? null : camera->GetActiveCamera();
        playerCone.IsVisible = active is not null;
        if (active is not null) playerCone.Rotation = MapRotationSign * -active->DirH - MapPlayerConeBaseAngle;
    }

    // The territory's map scale and offsets (cached for the last territory
    // asked about), for converting a world position to map-texture pixels:
    // pixel = (world + offset) * scale + 1024. Null when it has no usable map.
    private (float Scale, float OffsetX, float OffsetY)? ResolvePlayerMapInfo(uint territory)
    {
        if (playerMapTerritory != territory)
        {
            playerMapTerritory = territory;
            playerMapInfo = null;
            if (Services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(territory, out var row)
                && row.Map.ValueNullable is { } map && map.SizeFactor != 0)
                playerMapInfo = (map.SizeFactor / 100.0f, map.OffsetX, map.OffsetY);
        }
        return playerMapInfo;
    }

    private void DisposePlayerMarker()
    {
        playerCone?.Dispose();
        playerMarker?.Dispose();
        playerCone = null;
        playerMarker = null;
    }

    private void DisposeMarkerTooltips()
    {
        DisposePlayerMarker();
        foreach (var tip in markerTooltips.Values) tip.Dispose();
        markerTooltips.Clear();
        measuredTooltips.Clear();
        hoveredMarker = null;
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

    // The map is first centered while the window is still being built, before
    // the clip has any size (it is laid out afterwards), so that first centering
    // is computed against a 0x0 view and lands off-center. The first layout that
    // sizes the clip therefore centers on the same point again.
    private Vector2? lastCenteredFocus;
    private bool mapInitialCenterPending = true;

    private void CenterMapOn(Vector2 focusPixel)
    {
        // Zoomed all the way out the whole map is on show, so it is centered in the
        // panel like the game's own map; centering on a hole or a cluster of them
        // there just shoved the map sideways/up and left a black band along one edge.
        if (mapZoom <= MinMapZoom + 0.001f) focusPixel = new Vector2(1024.0f, 1024.0f);
        lastCenteredFocus = focusPixel;
        float scale = MapScale();
        float clipWidth = mapClip?.Width ?? EffectiveMapWidth(areaWidthSetting);
        float clipHeight = mapClip?.Height ?? EffectiveMapHeight();
        mapPanX = focusPixel.X - (clipWidth / scale) / 2.0f;
        mapPanY = focusPixel.Y - (clipHeight / scale) / 2.0f;
        ClampMapPan();
    }

    private void ClampMapPan()
    {
        float scale = MapScale();
        float clipWidth = mapClip?.Width ?? EffectiveMapWidth(areaWidthSetting);
        float clipHeight = mapClip?.Height ?? EffectiveMapHeight();
        float viewWidth = clipWidth / scale;
        float viewHeight = clipHeight / scale;
        (float minPanX, float maxPanX) = ClampedPanRange(viewWidth);
        (float minPanY, float maxPanY) = ClampedPanRange(viewHeight);
        mapPanX = Math.Clamp(mapPanX, minPanX, maxPanX);
        mapPanY = Math.Clamp(mapPanY, minPanY, maxPanY);
    }

    // Half a viewport's worth of slack past each edge once the viewport is
    // genuinely smaller than the 2048px map, so a point sitting right at (or
    // near) the map's edge or a corner can still be brought to dead center -
    // the same way the game's own World Map lets you pan past the drawn map
    // into its blank parchment margin. Once the viewport is wide/tall enough
    // to already show the whole map (the zoomed-out baseline, mapZoom==1),
    // though, there's no free space left to slide within, and giving the
    // same half-viewport slack there let CenterMapOn's per-spot centering
    // push the view sideways by however far a spot's pixel happened to sit
    // from the map's exact center - which cropped part of the real map off
    // one edge while opening up blank margin on the other for no reason.
    // That's what made the mini-map look like it was showing a completely
    // different, smaller stretch of coastline instead of the whole zone.
    // Pinning the range to the single "fits exactly, centered, no crop"
    // value whenever there's no free space left fixes that.
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

    // Jumps the journal straight to the region/area/fishing hole a map marker
    // was just clicked for. SelectRegion has its own logic for restoring
    // whatever spot was last viewed in that region, which would otherwise win
    // over the marker just clicked - SelectSpot is called again afterward so
    // the click always wins. SelectSpot itself updates the map to focus this
    // spot, so there's no separate ShowAreaMap call needed here.
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

    // Where the "Map" toggle sits at the bottom of the region list, in the
    // same row as (and mirrored from) the normal-log/guide buttons on the
    // opposite side of that row - not attached to the map panel at all
    // anymore, so it stays reachable even while the panel is collapsed.
    // The two buttons in the region column's bottom right corner (this map
    // toggle and the locate button just left of it, see
    // NativeJournalWindow.Locate.cs). Their 28px sprites have ~5px of clear
    // padding around a ~17px gold button, so to sit as a snug pair the boxes
    // overlap by 8px (a 3px gap between the visible buttons) and the pair is
    // pushed 3px past the column's right edge (leaving ~7px of visible margin
    // before the divider).
    private const float CornerButtonSize = 28.0f;
    private const float CornerButtonGap = -8.0f;
    private const float CornerButtonRightOverhang = 3.0f;

    private Vector2 MapButtonPosition()
    {
        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        // The region column starts at contentOrigin.X in window coordinates, so
        // its right edge (where the divider sits, 5px further on) is at
        // contentOrigin.X + regionWidth.
        return new Vector2(contentOrigin.X + regionWidth + CornerButtonRightOverhang - CornerButtonSize, FooterPosition.Y + (28.0f - CornerButtonSize) / 2.0f);
    }

    private void LayoutMapPanel()
    {
        // The button only makes sense once the feature is switched on in
        // Settings - with it off there's nothing here for it to toggle, so
        // it stays hidden and re-enabling lives in Settings only.
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

        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        float maximumAreaWidth = Math.Max(240.0f, ContentSize.X - regionWidth - 380.0f);
        float areaWidth = Math.Clamp(areaWidthSetting, 240.0f, Math.Min(500.0f, maximumAreaWidth));
        float areaX = regionWidth + ColumnGap;

        // Anchored to a fixed point derived from the window's own content
        // area, not from whether the panel is currently expanded (which
        // would move it, or push it out of reach entirely when the panel is
        // collapsed and reserves no space).
        //
        // This HAS to line up with the area list's own height (see
        // NativeJournalWindow.cs, which sizes it to
        // "ContentSize.Y - HeaderHeight - ReservedMapHeight()") - the list's
        // bottom edge always lands at exactly ContentSize.Y-ReservedMapHeight(),
        // regardless of where this method puts the map, so the map's own top
        // edge has to match that same point or the two overlap. A prior
        // attempt at centering this block in the full content height instead
        // (moving it up without also giving the list less height to compensate)
        // did exactly that - see PR/commit history around "map should be
        // centered" for the screenshot of the map floating over the area
        // list's own rows. Genuinely centering it needs the list to also give
        // up the extra space, not just moving the map on its own.
        float mapWidth = EffectiveMapWidth(areaWidth);
        float mapHeight = EffectiveMapHeight();
        float captionTop = ContentSize.Y - MapCaptionHeight - mapHeight;
        Vector2 dividerOffset = new(MapDividerOffsetX, MapDividerOffsetY);
        if (mapCaptionDivider is not null)
        {
            mapCaptionDivider.Position = contentOrigin + new Vector2(areaX, captionTop - MapDividerHeight) + dividerOffset;
            mapCaptionDivider.Width = areaWidth;
        }
        if (!mapOn) return;

        // The button used to sit to the left of this caption and reserve
        // space for itself - now that it lives at the bottom of the region
        // list instead, the caption text uses the whole column width.
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
        Vector2 mapImageOffset = new(MapImageOffsetX, MapImageOffsetY);
        if (mapClip is not null)
        {
            mapClip.Position = contentOrigin + new Vector2(areaX, captionTop + MapCaptionHeight - MapPanelSpacing) + mapImageOffset;
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

        // A simple gold frame around the clip rect, standing in for the
        // vanilla map window's own border. Each side has its own fixed
        // position (see the MapBorder* constants above), and each side's two
        // ends are independently extendable - so, for example, the top
        // line's left end and right end can be pulled in or stretched out
        // separately from one another, not just as one overall length.
        // Thickness is the one value shared by all four.
        float topPos = MapBorderTopPosition;
        float bottomPos = MapBorderBottomPosition;
        float leftPos = MapBorderLeftPosition;
        float rightPos = MapBorderRightPosition;
        float thickness = Math.Max(1.0f, MapBorderThickness);
        Vector2 clipPosition = contentOrigin + new Vector2(areaX, captionTop + MapCaptionHeight - MapPanelSpacing) + mapImageOffset;
        Vector2 clipSize = new(mapWidth, mapHeight);

        // Each side's line runs between two ends, expressed along the axis
        // that runs through the clip rect (0 = the clip's own left/top edge,
        // clipSize.X/Y = its right/bottom edge). The default (extend = 0)
        // lines every side up so the four corners meet neatly, using the
        // two sides that share that corner's own Positions.
        float topLeftX = -leftPos - MapBorderTopLeftExtend;
        float topRightX = clipSize.X + rightPos + MapBorderTopRightExtend;
        float bottomLeftX = -leftPos - MapBorderBottomLeftExtend;
        float bottomRightX = clipSize.X + rightPos + MapBorderBottomRightExtend;
        float leftTopY = -topPos - MapBorderLeftTopExtend;
        float leftBottomY = clipSize.Y + bottomPos + MapBorderLeftBottomExtend;
        float rightTopY = -topPos - MapBorderRightTopExtend;
        float rightBottomY = clipSize.Y + bottomPos + MapBorderRightBottomExtend;

        if (mapBorderTop is not null)
        {
            mapBorderTop.Height = thickness;
            mapBorderTop.Position = clipPosition + new Vector2(topLeftX, -topPos);
            mapBorderTop.Width = Math.Max(0.0f, topRightX - topLeftX);
        }
        if (mapBorderBottom is not null)
        {
            mapBorderBottom.Height = thickness;
            mapBorderBottom.Position = clipPosition + new Vector2(bottomLeftX, clipSize.Y + bottomPos - thickness);
            mapBorderBottom.Width = Math.Max(0.0f, bottomRightX - bottomLeftX);
        }
        if (mapBorderLeft is not null)
        {
            mapBorderLeft.Width = thickness;
            mapBorderLeft.Position = clipPosition + new Vector2(-leftPos, leftTopY);
            mapBorderLeft.Height = Math.Max(0.0f, leftBottomY - leftTopY);
        }
        if (mapBorderRight is not null)
        {
            mapBorderRight.Width = thickness;
            mapBorderRight.Position = clipPosition + new Vector2(clipSize.X + rightPos - thickness, rightTopY);
            mapBorderRight.Height = Math.Max(0.0f, rightBottomY - rightTopY);
        }

        ClampMapPan();
        ApplyMapPan();
    }

    // Manual drag-to-pan, scroll-to-zoom, and marker click handling, called
    // from OnDraw every frame - the map has no ImGui behind it, so
    // hit-testing is done by hand the same way the column dividers and fish
    // list already are.
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
                bool wasClick = mapDragDistance < 4.0f;
                draggingMap = false;
                if (wasClick) TryClickMarker(mouse, scale);
                return;
            }
            float deltaScreenX = mouse.PositionX - mapDragStartMouseX;
            float deltaScreenY = mouse.PositionY - mapDragStartMouseY;
            bool wasAlreadyDragging = mapDragDistance >= 4.0f;
            mapDragDistance = Math.Max(mapDragDistance, Math.Max(Math.Abs(deltaScreenX), Math.Abs(deltaScreenY)));
            // hoveredMarker is left as it was when the drag started, so the
            // tooltip stays attached to that marker as the map pans (it is
            // re-anchored in ApplyMapPan -> UpdateMarkerLayout below).
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
            float contentScaleBefore = MapScale();
            // The world-space point under the cursor before zooming - kept
            // pinned under the cursor afterward, the same way the real
            // game's map zoom feels, instead of always zooming toward the
            // map's center.
            Vector2 worldUnderCursor = new(mapPanX + localX / contentScaleBefore, mapPanY + localY / contentScaleBefore);
            float zoomStep = mouse.MouseWheel > 0 ? MapZoomStep : 1.0f / MapZoomStep;
            mapZoom = Math.Clamp(mapZoom * zoomStep, MinMapZoom, MaxMapZoom);
            float contentScaleAfter = MapScale();
            mapPanX = worldUnderCursor.X - localX / contentScaleAfter;
            mapPanY = worldUnderCursor.Y - localY / contentScaleAfter;
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

    private unsafe void TryClickMarker(CursorInputData mouse, float scale)
    {
        // The icon lives on the unscaled mapClip layer (see
        // RebuildMapMarkers), so its Size is already a plain screen-pixel
        // value - only the addon's own UI scale needs multiplying in here,
        // same as every other hand-tested hit box in this window.
        foreach (var (icon, data) in mapMarkers)
        {
            if (!icon.IsVisible) continue;
            Vector2 position = icon.ScreenPosition;
            Vector2 size = icon.Size * scale;
            if (mouse.PositionX >= position.X && mouse.PositionX <= position.X + size.X
                && mouse.PositionY >= position.Y && mouse.PositionY <= position.Y + size.Y)
            {
                NavigateToSpot(data.Spot);
                return;
            }
        }
    }

    private void DisposeMapPanel()
    {
        foreach (var (icon, data) in mapMarkers)
        {
            icon.Dispose();
            data.Circle.Dispose();
        }
        mapMarkers.Clear();
        DisposeMarkerTooltips();
        mapImage = null;
        mapBackdrop = null;
        mapContent = null;
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
