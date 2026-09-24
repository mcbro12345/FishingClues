using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Addon.Events;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

// per-frame update. Native buttons ignore clicks on rows clipped at a list's edge, so those are hit-tested by hand
public sealed partial class NativeJournalWindow
{
    private const float AreaHeaderTitleHeight = 28.0f;
    private const long AvailabilityRefreshIntervalMs = 1000;

    private FishEntryRowNode? partialHover;
    private ListButtonNode? partialAreaHover;

    protected override unsafe void OnDraw(AtkUnitBase* addon)
    {
        ClearManualHighlights();
        UpdateLocateButton();
        UpdateGameMapButton();
        ApplyPendingWork();
        RefreshAvailability();

        var framework = Framework.Instance();
        var mouse = framework == null ? default : framework->CursorInputs;
        var stage = AtkStage.Instance();
        bool overAddon = IsOverAddon(addon, stage);

        UpdateResizeCursor(addon, mouse, overAddon);
        if (!draggingDivider && overAddon)
        {
            HandleClippedFishRows(addon, mouse);
            HandleClippedAreaRows(addon, mouse);
            HandleSearchRightClick(addon, mouse);
            if ((mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0) BeginDividerDragIfHit(addon, mouse);
        }
        if (draggingDivider) UpdateDividerDrag(addon, mouse);
        UpdateMapInteraction(addon, mouse, stage);
        AnimateAreaList();
    }

    // clipped rows keep their native hover highlight, clear it and relight the real one
    private void ClearManualHighlights()
    {
        if (partialAreaHover is not null && spotButtons.ContainsKey(partialAreaHover)) partialAreaHover.HoverBackgroundNode.Alpha = 0;
        partialAreaHover = null;
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HoverBackgroundNode.Alpha = 0;
        partialHover = null;

        if (areaList is not null)
        {
            float offset = areaList.ScrollBarNode.ScrollPosition;
            foreach (var header in areaList.ContentNode.GetNodes<AnimatedAreaHeaderNode>())
            {
                float headerTop = header.Y - offset;
                if (IsClipped(headerTop, headerTop + AreaHeaderTitleHeight, areaList.Height))
                    header.HeaderTextureNode.AddColor = Vector3.Zero;
                if (header.IsCollapsed) continue;
                foreach (var spotButton in header.GetNodes<ListButtonNode>())
                {
                    float top = headerTop + spotButton.Y;
                    if (IsClipped(top, top + spotButton.Height, areaList.Height)) spotButton.HoverBackgroundNode.Alpha = 0;
                }
            }
        }
        if (fishList is not null)
        {
            float offset = fishList.ScrollBarNode.ScrollPosition;
            foreach (var row in fishButtons.Keys)
            {
                float top = row.Y - offset;
                if (IsClipped(top, top + row.Height, fishList.Height)) row.HoverBackgroundNode.Alpha = 0;
            }
        }
    }

    private void HidePartialHoverTooltip()
    {
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HideTooltip();
        partialHover = null;
    }

    private static unsafe bool IsOverAddon(AtkUnitBase* addon, AtkStage* stage)
        => stage != null && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon;

    private static unsafe void PlayClickSound() => UIGlobals.PlaySoundEffect(UiClickSoundEffectId);

    private static bool IsClipped(float top, float bottom, float listHeight) => top < 0 || bottom > listHeight;

    private void ApplyPendingWork()
    {
        SyncDetailsLayout();
        if (guideDetailsPending && selectedGuide is not null && guideLocation is not null)
        {
            guideDetailsPending = false;
            RenderCatchBody();
        }
        if (searchPending)
        {
            searchPending = false;
            RefreshSearch();
        }
    }

    private void RefreshAvailability()
    {
        if (options.GetAvailability is not { } getAvailability || availabilityRows.Count == 0
            || Environment.TickCount64 < nextAvailabilityRefresh) return;
        float heightBefore = fishButtons.Keys.Sum(row => row.Height);
        foreach (var (row, fish) in availabilityRows)
        {
            var info = getAvailability(fish);
            if (info is null) continue;
            row.SetAvailability(info.AvailableNow, info.BadgeText, info.Tooltip);
        }
        nextAvailabilityRefresh = Environment.TickCount64 + AvailabilityRefreshIntervalMs;
        // relayout resets the scrollbar, only do it when a height changed
        if (Math.Abs(fishButtons.Keys.Sum(row => row.Height) - heightBefore) > 0.5f) RefreshFishListLayout();
    }

    private static unsafe Vector2? PositionInList(ScrollingNode<JournalListNode> list, CursorInputData mouse, float addonScale)
    {
        float scale = Math.Max(0.1f, addonScale);
        Vector2 origin = list.ScreenPosition;
        float x = (mouse.PositionX - origin.X) / scale;
        float y = (mouse.PositionY - origin.Y) / scale;
        bool inside = x >= 0 && x < list.ContentNode.Width && y >= 0 && y < list.Height;
        return inside ? new Vector2(x, y) : null;
    }

    private unsafe void SetClickableCursor()
    {
        addonEvents.SetCursor(AddonCursorType.Clickable);
        ownsResizeCursor = true;
    }

    private unsafe void HandleClippedFishRows(AtkUnitBase* addon, CursorInputData mouse)
    {
        if (fishList is null || PositionInList(fishList, mouse, addon->Scale) is not { } cursor) return;
        float offset = fishList.ScrollBarNode.ScrollPosition;
        foreach (var row in fishButtons.Keys)
        {
            float top = row.Y - offset;
            float bottom = top + row.Height;
            if (!IsClipped(top, bottom, fishList.Height) || cursor.Y < Math.Max(0, top) || cursor.Y >= Math.Min(fishList.Height, bottom))
                continue;
            row.HoverBackgroundNode.Alpha = 1;
            partialHover = row;
            SetClickableCursor();
            if ((mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0)
            {
                // hand click skips the native sound
                PlayClickSound();
                row.OnClick?.Invoke();
            }
            break;
        }
    }

    // a header or hole row cut off by the bottom of the list draws fine but eats clicks
    private unsafe void HandleClippedAreaRows(AtkUnitBase* addon, CursorInputData mouse)
    {
        if (areaList is null || PositionInList(areaList, mouse, addon->Scale) is not { } cursor) return;
        float offset = areaList.ScrollBarNode.ScrollPosition;
        bool clicked = (mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0;
        foreach (var header in areaList.ContentNode.GetNodes<AnimatedAreaHeaderNode>())
        {
            float headerTop = header.Y - offset;
            // only the title strip counts, a tall expanded header isn't clipped
            float titleBottom = headerTop + AreaHeaderTitleHeight;
            if (IsClipped(headerTop, titleBottom, areaList.Height))
            {
                if (cursor.Y < Math.Max(0, headerTop) || cursor.Y >= Math.Min(areaList.Height, titleBottom)) continue;
                // the same +16 the header's own hover animation adds
                header.HeaderTextureNode.AddColor = new Vector3(16.0f / 255.0f);
                SetClickableCursor();
                if (clicked && header.IsCollapsed) header.IsCollapsed = false;
                return;
            }
            if (header.IsCollapsed) continue;
            foreach (var spotButton in header.GetNodes<ListButtonNode>())
            {
                float top = headerTop + spotButton.Y;
                float bottom = top + spotButton.Height;
                if (!IsClipped(top, bottom, areaList.Height) || cursor.Y < Math.Max(0, top) || cursor.Y >= Math.Min(areaList.Height, bottom))
                    continue;
                spotButton.HoverBackgroundNode.Alpha = 1;
                partialAreaHover = spotButton;
                SetClickableCursor();
                if (clicked)
                {
                    PlayClickSound();
                    spotButton.OnClick?.Invoke();
                }
                return;
            }
        }
    }

    private unsafe void HandleSearchRightClick(AtkUnitBase* addon, CursorInputData mouse)
    {
        if (searchInput is null || (mouse.MouseButtonPressedFlags & MouseButtonFlags.RBUTTON) == 0) return;
        Vector2 position = searchInput.ScreenPosition;
        float scale = Math.Max(0.1f, addon->Scale);
        bool over = mouse.PositionX >= position.X && mouse.PositionX <= position.X + searchInput.Width * scale
            && mouse.PositionY >= position.Y && mouse.PositionY <= position.Y + searchInput.Height * scale;
        if (!over) return;
        searchInput.String = "";
        SubmitSearchText("");
    }

    private void AnimateAreaList()
    {
        if (areaList is null) return;
        bool animating = false;
        foreach (var header in areaList.ContentNode.GetNodes<AnimatedAreaHeaderNode>())
            animating |= header.Tick();
        float previousHeight = areaList.ContentNode.Height;
        areaList.ContentNode.RecalculateLayout();
        if (animating || previousHeight != areaList.ContentNode.Height) areaList.RecalculateSizes();
        // relayout resets header X, reapply the inset
        ReapplyDropdownLeftInset();
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        catchBody = null;
        ReleaseResizeCursor();
        SaveViewState();
        base.OnFinalize(addon);
        regionList = null;
        areaList = null;
        fishList = null;
        detailsList = null;
        detailsDivider = null;
        detailsBottomDivider = null;
        dividerHandle = null;
        regionDividerHandle = areaDividerHandle = null;
        draggingDivider = false;
        detailsHint = null;
        regionButtons.Clear();
        spotButtons.Clear();
        fishButtons.Clear();
        selectedDetails = null;
        selectedGuide = null;
        guideLocation = null;
        guideDetailsPending = false;
        regionHeader = null;
        areaHeader = null;
        fishHeader = null;
        locateButton = null;
        settingsButton = null;
        currentHole = null;
        spotTitle = null;
        spotSummary = null;
        summaryDivider = null;
        areaHeaderDivider = null;
        regionDivider = null;
        fishDivider = null;
        normalLogButton = null;
        guideButton = null;
        searchInput = null;
        searchButton = null;
        searchPending = false;
        DisposeMapPanel();
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        HidePartialHoverTooltip();
        if (partialAreaHover is not null && spotButtons.ContainsKey(partialAreaHover)) partialAreaHover.HoverBackgroundNode.Alpha = 0;
        partialAreaHover = null;
        ReleaseResizeCursor();
        if (draggingDivider)
        {
            draggingDivider = false;
            options.SaveLayout();
        }
        SaveViewState();
    }

    private void SaveViewState()
    {
        if (fishList is null) return;
        sessionState.SelectedFish = selectedFish;
        sessionState.RegionScroll = regionList?.ScrollBarNode.ScrollPosition ?? 0;
        sessionState.AreaScroll = areaList?.ScrollBarNode.ScrollPosition ?? 0;
        sessionState.FishScroll = fishList.ScrollBarNode.ScrollPosition;
        sessionState.DetailsScroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;
        if (mapArea is not null && !restoreMapView)
        {
            sessionState.MapArea = mapArea.Name;
            sessionState.MapZoom = mapZoom;
            sessionState.MapPanX = mapPanX;
            sessionState.MapPanY = mapPanY;
        }
        if (!GuideMode && selectedRegion is not null)
            sessionState.Regions[selectedRegion.Name] = new RegionViewState(selectedSpot?.Id ?? 0, selectedFish,
                sessionState.AreaScroll, sessionState.FishScroll, sessionState.DetailsScroll);
    }
}
