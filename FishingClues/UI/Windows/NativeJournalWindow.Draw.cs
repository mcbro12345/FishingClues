using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using Dalamud.Game.Addon.Events;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.UI.Components;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

public sealed partial class NativeJournalWindow
{
    private FishEntryRowNode? partialHover;
    // The manually highlighted, partly clipped area-list hole row (its native
    // hover never fires, so its highlight has to be cleared by hand too).
    private ListButtonNode? partialAreaHover;

    // A row that is only partly inside its list's clip region gets the native
    // mouse-over (highlight on) but never the mouse-out, so its highlight
    // stays lit after the cursor leaves. Every frame, drop the highlight on
    // all such rows; the manual hover pass below re-lights the one that is
    // really under the cursor.
    private void ClearClippedHoverHighlights()
    {
        if (areaList is not null)
        {
            float offset = areaList.ScrollBarNode.ScrollPosition;
            foreach (var header in areaList.ContentNode.GetNodes<AnimatedAreaHeaderNode>())
            {
                float headerTop = header.Y - offset;
                // The header's own title strip (28px) - lit by hand below when
                // it is partly clipped, so it has to be un-lit by hand too.
                if (headerTop < 0 || headerTop + 28.0f > areaList.Height) header.HeaderTextureNode.AddColor = System.Numerics.Vector3.Zero;
                if (header.IsCollapsed) continue;
                foreach (var spotButton in header.GetNodes<ListButtonNode>())
                {
                    float top = headerTop + spotButton.Y;
                    if (top < 0 || top + spotButton.Height > areaList.Height) spotButton.HoverBackgroundNode.Alpha = 0;
                }
            }
        }
        if (fishList is not null)
        {
            float offset = fishList.ScrollBarNode.ScrollPosition;
            foreach (var row in fishButtons.Keys)
            {
                float top = row.Y - offset;
                if (top < 0 || top + row.Height > fishList.Height) row.HoverBackgroundNode.Alpha = 0;
            }
        }
    }

    protected override unsafe void OnDraw(AtkUnitBase* addon)
    {
        if (partialAreaHover is not null && spotButtons.ContainsKey(partialAreaHover)) partialAreaHover.HoverBackgroundNode.Alpha = 0;
        partialAreaHover = null;
        ClearClippedHoverHighlights();
        UpdateLocateButton();
        var previousPartialHover = partialHover;
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HoverBackgroundNode.Alpha = 0;
        partialHover = null;
        if (guideDetailsPending && selectedGuide is not null && guideLocation is not null) {
            guideDetailsPending = false;
            if (guideLocationPending) {
                guideLocationPending = false;
                guidePoles = selectedGuide.GetPoles(guideLocation);
                guidePole = guidePoles.FirstOrDefault();
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
                        if ((mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0)
                        {
                            // A normal row click plays its sound as part of the native button
                            // reaction; invoking OnClick by hand skips that, so play it here
                            // (as the area list's manual clicks do).
                            UIGlobals.PlaySoundEffect(UiClickSoundEffectId);
                            row.OnClick?.Invoke();
                        }
                        break;
                    }
                }
            }
        }
        // Same native quirk as the fishList block above (a button whose own
        // bounds are only partially inside its scrolling clip region never
        // receives the click at all), applied to the area list - which
        // needs it far more often now that the map panel usually sits open
        // underneath it (see ReservedMapHeight): whatever area happens to
        // land right where the shrunk-down list's bottom edge falls (an
        // area header, or a discovered hole's row inside the one area
        // that's currently expanded) renders its label just fine but
        // silently eats every click, exactly like "Moraby Drydocks" being
        // impossible to open. A collapsed header just gets un-collapsed
        // directly (equivalent to what its own native click would have
        // triggered); an expanded header's inner hole rows are checked the
        // same way the fish rows are, since they're only ever partially
        // clipped when the area itself is the one straddling the edge.
        if (!draggingDivider && areaList is not null && stage != null
            && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon)
        {
            float areaScale = Math.Max(0.1f, addon->Scale);
            Vector2 areaOrigin = areaList.ScreenPosition;
            float areaX = (mouse.PositionX - areaOrigin.X) / areaScale;
            float areaY = (mouse.PositionY - areaOrigin.Y) / areaScale;
            if (areaX >= 0 && areaX < areaList.ContentNode.Width && areaY >= 0 && areaY < areaList.Height)
            {
                float areaOffset = areaList.ScrollBarNode.ScrollPosition;
                bool areaClicked = (mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0;
                foreach (var header in areaList.ContentNode.GetNodes<AnimatedAreaHeaderNode>())
                {
                    float headerTop = header.Y - areaOffset;
                    // Only the header's own clickable title strip (see
                    // AnimatedAreaHeaderNode.OnRecalculateLayout's hardcoded
                    // 28px title height - the same value whether the header
                    // is collapsed or expanded, since Height there IS 28 when
                    // collapsed) matters for "is the thing you'd click to
                    // expand/collapse this header partially clipped". Using
                    // header.Height here instead - the FULL expanded block,
                    // title plus every child row - was the bug behind
                    // "Moraby Drydocks unclickable": whenever an expanded
                    // header's total height overflowed the list (because its
                    // LAST row sat at the clipped edge), every row under that
                    // header, including ones sitting comfortably in full
                    // view earlier in the list, got funneled into this
                    // title-only branch instead of reaching the per-row
                    // highlight/click handling below.
                    const float headerTitleHeight = 28.0f;
                    float headerTitleBottom = headerTop + headerTitleHeight;
                    bool headerTitlePartial = headerTop < 0 || headerTitleBottom > areaList.Height;
                    if (headerTitlePartial)
                    {
                        if (areaY >= Math.Max(0, headerTop) && areaY < Math.Min(areaList.Height, headerTitleBottom))
                        {
                            // Same +16 add-color the header's own hover animation
                            // applies.
                            header.HeaderTextureNode.AddColor = new System.Numerics.Vector3(16.0f / 255.0f);
                            addonEvents.SetCursor(AddonCursorType.Clickable);
                            ownsResizeCursor = true;
                            if (areaClicked && header.IsCollapsed) header.IsCollapsed = false;
                            break;
                        }
                        continue;
                    }
                    if (header.IsCollapsed) continue;
                    bool foundSpot = false;
                    foreach (var spotButton in header.GetNodes<ListButtonNode>())
                    {
                        float spotTop = headerTop + spotButton.Y;
                        float spotBottom = spotTop + spotButton.Height;
                        bool spotPartial = spotTop < 0 || spotBottom > areaList.Height;
                        if (spotPartial && areaY >= Math.Max(0, spotTop) && areaY < Math.Min(areaList.Height, spotBottom))
                        {
                            spotButton.HoverBackgroundNode.Alpha = 1;
                            partialAreaHover = spotButton;
                            addonEvents.SetCursor(AddonCursorType.Clickable);
                            ownsResizeCursor = true;
                            if (areaClicked)
                            {
                                // A normal, non-clipped row click plays its
                                // sound automatically as part of the native
                                // AtkComponentButton click reaction - calling
                                // OnClick directly here, bypassing that
                                // native dispatch entirely (the whole reason
                                // this manual path exists), skips it too, so
                                // it needs to be triggered by hand.
                                UIGlobals.PlaySoundEffect(UiClickSoundEffectId);
                                spotButton.OnClick?.Invoke();
                            }
                            foundSpot = true;
                            break;
                        }
                    }
                    if (foundSpot) break;
                }
            }
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
            if (!mouse.IsGameWindowFocused || (mouse.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) == 0 || configuration.IsDividerLocked(dragKind))
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
        UpdateMapInteraction(addon, mouse, stage);
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
            // RecalculateSizes() above (when it runs) internally re-triggers
            // ContentNode.RecalculateLayout(), which resets every header's X back
            // to 0 - reapply the inset after that, not before, so it isn't wiped
            // out again by the very call meant to size things correctly.
            ReapplyDropdownLeftInset();
        }
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
        guidePole = null;
        guidePoles = Array.Empty<FishingPole>();
        guideDetailsPending = guideLocationPending = false;
        regionHeader = null;
        areaHeader = null;
        fishHeader = null;
        locateButton = null;
        settingsButton = null;
        currentHole = null;
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
        DisposeMapPanel();
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HideTooltip();
        partialHover = null;
        if (partialAreaHover is not null && spotButtons.ContainsKey(partialAreaHover)) partialAreaHover.HoverBackgroundNode.Alpha = 0;
        partialAreaHover = null;
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
