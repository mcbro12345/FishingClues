using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using Dalamud.Game.Addon.Events;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace FishingClues;

public sealed partial class NativeJournalWindow
{
    private FishEntryRowNode? partialHover;

    protected override unsafe void OnDraw(AtkUnitBase* addon)
    {
        var previousPartialHover = partialHover;
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HoverBackgroundNode.Alpha = 0;
        partialHover = null;
        if (guideDetailsPending && selectedGuide is not null && guideLocation is not null) {
            guideDetailsPending = false;
            if (guideLocationPending) {
                guideLocationPending = false;
                guidePoles = selectedGuide.GetPoles(guideLocation);
                guidePole = guidePoles.FirstOrDefault();
                poleSelector?.SetOptions(guidePoles, guidePole);
            }
            RenderCatchBody();
        }
        if (searchPending) {
            searchPending = false;
            RefreshSearch();
        }
        if (getAvailability is not null && availabilityRows.Count > 0 && Environment.TickCount64 >= nextAvailabilityRefresh)
        {
            nextAvailabilityRefresh = Environment.TickCount64 + 30000;
            foreach (var (row, fish) in availabilityRows)
            {
                var info = getAvailability(fish);
                if (info is not null) row.SetAvailability(info.AvailableNow, info.BadgeText, info.Tooltip);
            }
            RefreshFishListLayout();
        }
        var framework = Framework.Instance();
        var mouse = framework == null ? default : framework->CursorInputs;
        var stage = AtkStage.Instance();
        int cursorKind = -1;
        if (mouse.IsGameWindowFocused)
        {
            if (draggingDivider && !configuration.IsDividerLocked(dragKind)) cursorKind = dragKind;
            else if (stage != null && stage->AtkCollisionManager != null
                && stage->AtkCollisionManager->IntersectingAddon == addon)
            {
                if (HitDivider(regionDividerHandle, mouse, addon->Scale)) cursorKind = 1;
                else if (HitDivider(areaDividerHandle, mouse, addon->Scale)) cursorKind = 2;
                else if (HitDivider(dividerHandle, mouse, addon->Scale)) cursorKind = 0;
            }
        }
        if (cursorKind >= 0)
        {
            addonEvents.SetCursor(cursorKind == 0 ? AddonCursorType.ResizeNS : AddonCursorType.ResizeWE);
            ownsResizeCursor = true;
        }
        else ReleaseResizeCursor();
        if (!draggingDivider && fishList is not null && stage != null
            && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon)
        {
            // native buttons reject clicks when only part of their bounds is clipped
            float scale = Math.Max(0.1f, addon->Scale);
            Vector2 origin = fishList.ScreenPosition;
            float x = (mouse.PositionX - origin.X) / scale;
            float y = (mouse.PositionY - origin.Y) / scale;
            if (x >= 0 && x < fishList.ContentNode.Width && y >= 0 && y < fishList.Height)
            {
                float offset = fishList.ScrollBarNode.ScrollPosition;
                foreach (var row in fishButtons.Keys)
                {
                    float top = row.Y - offset;
                    float bottom = top + row.Height;
                    bool partial = top < 0 || bottom > fishList.Height;
                    if (partial && y >= Math.Max(0, top) && y < Math.Min(fishList.Height, bottom))
                    {
                        row.HoverBackgroundNode.Alpha = 1;
                        partialHover = row;
                        addonEvents.SetCursor(AddonCursorType.Clickable);
                        ownsResizeCursor = true;
                        if ((mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0) row.OnClick?.Invoke();
                        break;
                    }
                }
            }
        }
        if (!ReferenceEquals(previousPartialHover, partialHover)) {
            if (previousPartialHover is not null && fishButtons.ContainsKey(previousPartialHover)) previousPartialHover.HideTooltip();
            partialHover?.ShowTooltip();
        }
        if (!draggingDivider && stage != null
            && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon
            && (mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0)
        {
            if (HitDivider(regionDividerHandle, mouse, addon->Scale)) BeginDividerDrag(1);
            else if (HitDivider(areaDividerHandle, mouse, addon->Scale)) BeginDividerDrag(2);
            else if (HitDivider(dividerHandle, mouse, addon->Scale)) BeginDividerDrag(0);
        }
        if (draggingDivider)
        {
            if (!mouse.IsGameWindowFocused || (mouse.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) == 0 || configuration.IsDividerLocked(dragKind) || (dragKind == 0 && !configuration.EmbedFishDetails))
            {
                draggingDivider = false;
                saveDivider();
            }
            else
            {
                if (dragKind == 0)
                {
                    float body = Math.Max(212, ContentSize.Y - HeaderHeight - FishSummaryHeight);
                    float delta = (mouse.PositionY - dragStartY) / Math.Max(0.1f, addon->Scale);
                    configuration.DetailsHeightRatio = Math.Clamp(dragStartRatio - delta / body, 100 / body, 1 - 112 / body);
                }
                else
                {
                    float delta = (mouse.PositionX - dragStartX) / Math.Max(0.1f, addon->Scale);
                    if (dragKind == 1)
                        configuration.NativeRegionWidth = regionWidthSetting = Math.Clamp(dragStartWidth + delta, 130, 280);
                    else
                    {
                        float maximum = Math.Min(500, Math.Max(240, ContentSize.X - regionWidthSetting - 380));
                        configuration.NativeAreaWidth = areaWidthSetting = Math.Clamp(dragStartWidth + delta, 240, maximum);
                    }
                }
                float scroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;
                float fishScroll = fishList?.ScrollBarNode.ScrollPosition ?? 0;
                LayoutAttachedNodes();
                if (detailsList is not null) RestoreScroll(detailsList, scroll);
                if (fishList is not null) RestoreScroll(fishList, fishScroll);
            }
        }
        bool animating = false;
        if (areaList is not null)
        {
            foreach (var header in areaList.ContentNode.GetNodes<AnimatedAreaHeaderNode>())
                animating |= header.Tick();
            float previousHeight = areaList.ContentNode.Height;
            areaList.ContentNode.RecalculateLayout();
            if (animating || previousHeight != areaList.ContentNode.Height)
            {
                areaList.RecalculateSizes();

            }
        }
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        catchBody = null;
        poleSelector = null;
        ReleaseResizeCursor();
        SaveViewState();
        base.OnFinalize(addon);
        regionList = null;
        areaList = null;
        fishList = null;
        detailsList = null;
        detailsDivider = null;
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
        guidePole = null;
        guidePoles = Array.Empty<FishingPole>();
        guideDetailsPending = guideLocationPending = false;
        regionHeader = null;
        areaHeader = null;
        fishHeader = null;
        spotTitle = null;
        spotSummary = null;
        summaryDivider = null;
        regionDivider = null;
        fishDivider = null;
        normalLogButton = null;
        guideButton = null;
        searchInput = null;
        searchButton = null;
        searchPending = false;
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HideTooltip();
        partialHover = null;
        ReleaseResizeCursor();
        if (draggingDivider) { draggingDivider = false; saveDivider(); }
        SaveViewState();
    }

    private void ReleaseResizeCursor()
    {
        if (!ownsResizeCursor) return;
        addonEvents.ResetCursor();
        ownsResizeCursor = false;
    }

    private void SaveViewState()
    {
        if (fishList is null) return;
        sessionState.SelectedFish = selectedFish;
        sessionState.RegionScroll = regionList?.ScrollBarNode.ScrollPosition ?? 0;
        sessionState.AreaScroll = areaList?.ScrollBarNode.ScrollPosition ?? 0;
        sessionState.FishScroll = fishList.ScrollBarNode.ScrollPosition;
        sessionState.DetailsScroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;
        if (!GuideMode && selectedRegion is not null)
            sessionState.Regions[selectedRegion.Name] = new RegionViewState(selectedSpot?.Id ?? 0, selectedFish,
                sessionState.AreaScroll, sessionState.FishScroll, sessionState.DetailsScroll);
    }
}
