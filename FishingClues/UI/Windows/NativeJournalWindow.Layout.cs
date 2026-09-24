using System;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

// layout pass, column widths, dropdown widths
public sealed partial class NativeJournalWindow
{
    public unsafe string DescribeDetailsLayout()
    {
        if (fishList is null || detailsList is null || detailsDivider is null || detailsBottomDivider is null)
            return "Details layout: window not built.";
        AtkResNode* divider = detailsDivider;
        AtkResNode* bottom = detailsBottomDivider;
        return $"Details layout ({(GuideMode ? "guide" : "journal")}): open={DetailsOpen}, ratio={configuration.DetailsHeightRatio:0.000}, "
            + $"fishList y={fishList.Y:0.###} h={fishList.Height:0.###}, "
            + $"topDivider y={detailsDivider.Y:0.###} w={detailsDivider.Width:0.###} h={detailsDivider.Height:0.###} visible={detailsDivider.IsVisible} alpha={detailsDivider.Alpha:0.##} screenY={divider->ScreenY:0.#}, "
            + $"detailsList y={detailsList.Y:0.###} h={detailsList.Height:0.###} alpha={detailsList.Alpha:0.##}, "
            + $"bottomDivider y={detailsBottomDivider.Y:0.###} visible={detailsBottomDivider.IsVisible} screenY={bottom->ScreenY:0.#}";
    }

    private (float Region, float Area) ColumnWidths()
    {
        float region = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        float maximumArea = Math.Max(240.0f, ContentSize.X - region - 380.0f);
        return (region, Math.Clamp(areaWidthSetting, 240.0f, Math.Min(500.0f, maximumArea)));
    }

    public void ApplyLayout(float windowWidth, float windowHeight, float regionWidth, float areaWidth,
        float dropdownLeftInset, float dropdownRightInset, bool configuredShowButton)
    {
        regionWidthSetting = regionWidth;
        areaWidthSetting = areaWidth;
        dropdownLeftInsetSetting = dropdownLeftInset;
        dropdownRightInsetSetting = dropdownRightInset;
        showNormalLogButton = configuredShowButton;
        Size = new Vector2(windowWidth, windowHeight);
        nextAvailabilityRefresh = 0;
        if (!IsOpen)
            return;

        // relayout resets scroll, keep it
        KeepingScroll(() =>
        {
            SetWindowSize(Size);
            LayoutAttachedNodes();
            RefreshSelectedDetails();
        }, regionList, areaList, fishList, detailsList);
    }

    private void KeepingScroll(Action relayout, params ScrollingNode<JournalListNode>?[] lists)
    {
        var saved = lists.Select(list => list?.ScrollBarNode.ScrollPosition ?? 0).ToArray();
        relayout();
        for (int i = 0; i < lists.Length; i++)
            RestoreScroll(lists[i], saved[i]);
    }

    private void RefreshSelectedDetails()
    {
        if (selectedFish == 0) return;
        JournalFish? fish = GuideMode
            ? options.GuideFish?.FirstOrDefault(f => f.FishParameterId == selectedFish)
            : selectedSpot?.Fish.FirstOrDefault(f => f.FishParameterId == selectedFish);
        if (fish is null) return;

        selectedDetails = options.BuildDetails(fish);
        if (options.BuildGuideDetails is not null)
        {
            string? locationLabel = guideLocation?.Label;
            selectedGuide = options.BuildGuideDetails(fish);
            guideLocation = locationLabel is null ? selectedGuide.Locations.FirstOrDefault()
                : selectedGuide.Locations.FirstOrDefault(l => l.Label == locationLabel) ?? selectedGuide.Locations.FirstOrDefault();
        }
        RenderDetails();
    }

    private void LayoutAttachedNodes()
    {
        if (regionList is null || areaList is null || fishList is null || layingOut)
            return;
        layingOut = true;
        try { LayoutAttachedNodesCore(); }
        finally { layingOut = false; }
    }

    private void LayoutAttachedNodesCore()
    {
        if (regionList is null || areaList is null || fishList is null) return;
        if (selectedSpot is not null && renderedUncaughtFirst != configuration.UncaughtFishFirst)
        {
            var details = selectedDetails;
            var guide = selectedGuide;
            var location = guideLocation;
            uint fish = selectedFish;
            SelectSpot(selectedSpot, force: true);
            selectedDetails = details;
            selectedGuide = guide;
            guideLocation = location;
            selectedFish = fish;
            UpdateFishSelection();
        }

        contentOrigin = ContentStartPosition;
        var (regionWidth, areaWidth) = ColumnWidths();
        float fishX = GuideMode ? 0 : regionWidth + areaWidth + ColumnGap * 2.0f;
        float listHeight = Math.Max(100.0f, ContentSize.Y - HeaderHeight);
        float regionListHeight = Math.Max(100.0f, listHeight - (showNormalLogButton || options.OpenGuide is not null ? 48.0f : 0.0f));
        float fishWidth = Math.Max(100.0f, ContentSize.X - fishX);

        SetHeaderLayout(regionHeader, 0.0f, regionWidth);
        SetHeaderLayout(areaHeader, regionWidth + ColumnGap, areaWidth);
        SetHeaderLayout(fishHeader, fishX, fishWidth);
        PositionLocateButton();

        regionList.Position = contentOrigin + new Vector2(0.0f, HeaderHeight);
        regionList.Size = new Vector2(regionWidth, regionListHeight);
        areaList.Position = contentOrigin + new Vector2(regionWidth + ColumnGap, HeaderHeight);
        areaList.Size = new Vector2(areaWidth, Math.Max(100.0f, listHeight - ReservedMapHeight()));
        if (spotTitle is not null)
        {
            spotTitle.Position = contentOrigin + new Vector2(fishX, HeaderHeight);
            spotTitle.Width = fishWidth - 16;
        }
        if (spotSummary is not null)
        {
            spotSummary.Position = contentOrigin + new Vector2(fishX, HeaderHeight + 26);
            spotSummary.Width = fishWidth - 16;
        }
        if (summaryDivider is not null)
        {
            // same divider separates the search bar from the results in the guide
            summaryDivider.Position = contentOrigin + new Vector2(fishX, GuideMode ? SearchDividerY : HeaderHeight + 52);
            summaryDivider.Width = GuideMode ? fishWidth : fishWidth - 10;
            summaryDivider.IsVisible = true;
        }
        regionList.IsVisible = areaList.IsVisible = !GuideMode;
        if (regionHeader is not null) regionHeader.IsVisible = !GuideMode;
        if (areaHeader is not null) areaHeader.IsVisible = !GuideMode;
        if (fishHeader is not null) fishHeader.IsVisible = !GuideMode;
        if (spotTitle is not null) spotTitle.IsVisible = !GuideMode;
        if (spotSummary is not null) spotSummary.IsVisible = !GuideMode;
        if (areaHeaderDivider is not null)
        {
            areaHeaderDivider.Position = contentOrigin + new Vector2(regionWidth + ColumnGap, HeaderHeight - 2.0f);
            areaHeaderDivider.Width = areaWidth;
            areaHeaderDivider.IsVisible = !GuideMode;
        }
        if (searchInput is not null) {
            searchInput.Position = contentOrigin;
            searchInput.Width = Math.Max(100, fishWidth - SearchButtonWidth);
            if (searchButton is not null)
            {
                searchButton.Width = SearchButtonWidth;
                searchButton.Position = contentOrigin + new Vector2(searchInput.Width, 3.5f);
            }
        }
        float fishBodyHeight = listHeight - (GuideMode ? 8 : FishSummaryHeight);
        fishList.Position = contentOrigin + new Vector2(fishX, HeaderHeight + (GuideMode ? 8 : FishSummaryHeight));
        fishList.Size = new Vector2(fishWidth, fishBodyHeight);
        RefreshFishListLayout();
        if (detailsList is not null && detailsDivider is not null)
        {
            // no fish selected: shrink to a strip with the hint, saved ratio untouched
            detailsLaidOutOpen = DetailsOpen;
            float ratio = float.IsFinite(configuration.DetailsHeightRatio) ? configuration.DetailsHeightRatio : 0.38f;
            float detailsHeight = DetailsOpen
                ? Math.Clamp(fishBodyHeight * ratio, 100, Math.Max(100, fishBodyHeight - 112))
                : CollapsedDetailsHeight;
            detailsHeight = MathF.Round(detailsHeight);
            float fishHeight = MathF.Round(fishBodyHeight - detailsHeight - 12.0f);
            detailsList.IsVisible = true;
            detailsDivider.IsVisible = true;
            if (dividerHandle is not null)
            {
                SetDividerHandleInteractive(dividerHandle, DetailsOpen && !configuration.IsDividerLocked(0));
                dividerHandle.ShowClickableCursor = false;
                dividerHandle.Position = fishList.Position + new Vector2(0, fishHeight);
                dividerHandle.Size = new Vector2(fishWidth, 12);
            }
            // new sizes apply at once but redraw a frame late, so while dragging use last frame's sizes to keep each list's edge on its divider
            float appliedFishHeight = fishHeight, appliedDetailsHeight = detailsHeight;
            if (draggingDivider && dragKind == 0 && !float.IsNaN(previousFishHeight))
            {
                appliedFishHeight = previousFishHeight;
                appliedDetailsHeight = previousDetailsHeight;
            }
            previousFishHeight = fishHeight;
            previousDetailsHeight = detailsHeight;
            fishList.Height = appliedFishHeight;
            detailsDivider.Position = fishList.Position + new Vector2(0, fishHeight);
            detailsDivider.Width = fishWidth;
            // runs between the dividers so scrolled text is cut flush
            Vector2 panelOrigin = fishList.Position + new Vector2(0, fishHeight + 2.0f);
            detailsList.Position = panelOrigin + new Vector2(8, 0);
            detailsList.Size = new Vector2(fishWidth - 16, appliedDetailsHeight + 8.0f);
            if (detailsBottomDivider is not null)
            {
                detailsBottomDivider.IsVisible = true;
                detailsBottomDivider.Position = fishList.Position + new Vector2(0, fishHeight + detailsHeight + 10.0f);
                detailsBottomDivider.Width = fishWidth;
            }
            ReflowDetails();
        }

        SetDividerLayout(regionDivider, regionWidth + ColumnGap / 2.0f);
        SetDividerLayout(fishDivider, regionWidth + areaWidth + ColumnGap * 1.5f);
        if (regionDividerHandle is not null)
        {
            regionDividerHandle.Position = contentOrigin + new Vector2(regionWidth, 0);
            regionDividerHandle.Size = new Vector2(ColumnGap, ContentSize.Y);
            SetDividerHandleInteractive(regionDividerHandle, !configuration.IsDividerLocked(1));
            regionDividerHandle.ShowClickableCursor = false;
        }
        if (areaDividerHandle is not null)
        {
            areaDividerHandle.Position = contentOrigin + new Vector2(regionWidth + areaWidth + ColumnGap, 0);
            areaDividerHandle.Size = new Vector2(ColumnGap, ContentSize.Y);
            SetDividerHandleInteractive(areaDividerHandle, !configuration.IsDividerLocked(2));
            areaDividerHandle.ShowClickableCursor = false;
        }
        if (normalLogButton is not null)
        {
            normalLogButton.Position = FooterPosition;
            normalLogButton.IsVisible = showNormalLogButton;
        }
        if (guideButton is not null) guideButton.Position = FooterPosition + new Vector2(34, 0);
        if (GuideMode) {
            if (regionDivider is not null) regionDivider.IsVisible = false;
            if (fishDivider is not null) fishDivider.IsVisible = false;
            if (regionDividerHandle is not null) regionDividerHandle.IsVisible = false;
            if (areaDividerHandle is not null) areaDividerHandle.IsVisible = false;
        }
        ApplyAreaDropdownWidths();
        regionList.RecalculateSizes();
        RefreshFishListLayout();
        // native ellipsis overwrites the text buffer, so restore from the model, not the label
        foreach (var pair in regionButtons)
            pair.Key.String = pair.Value.IsUnlocked ? pair.Value.Name : "???";
        if (configuration.IsDividerLocked(dragKind)) ReleaseResizeCursor();
        LayoutMapPanel();
    }

    private const float MinDropdownWidth = 80.0f;
    private const float MaxDropdownInset = 150.0f;

    // inset is relative to a baseline (see Configuration), can be negative
    private float EffectiveLeftInset() => Math.Clamp(
        dropdownLeftInsetSetting + Configuration.DropdownLeftInsetBaseline, -MaxDropdownInset, MaxDropdownInset);
    private float EffectiveRightInset() => Math.Clamp(
        dropdownRightInsetSetting + Configuration.DropdownRightInsetBaseline, -MaxDropdownInset, MaxDropdownInset);

    private float EffectiveDropdownWidth()
    {
        float availableWidth = areaList is null ? areaWidthSetting : areaList.ContentNode.Width;
        float usable = availableWidth - 12.0f - EffectiveLeftInset() - EffectiveRightInset();
        return Math.Max(MinDropdownWidth, usable);
    }

    private void ApplyAreaDropdownWidths()
    {
        if (areaList is null)
            return;
        float width = EffectiveDropdownWidth();
        foreach (CollapsingHeaderNode header in areaList.ContentNode.GetNodes<CollapsingHeaderNode>())
            header.Width = width;
        areaList.ContentNode.RecalculateLayout();
        areaList.RecalculateSizes();
        // RecalculateSizes resets header X, apply the inset after
        ReapplyDropdownLeftInset();
    }

    private void ReapplyDropdownLeftInset()
    {
        if (areaList is null) return;
        float leftInset = EffectiveLeftInset();
        foreach (CollapsingHeaderNode header in areaList.ContentNode.GetNodes<CollapsingHeaderNode>())
            header.X = leftInset;
    }

    private void SetHeaderLayout(CategoryTextNode? header, float x, float width)
    {
        if (header is null)
            return;
        header.Position = contentOrigin + new Vector2(x, 0.0f);
        header.Size = new Vector2(width, 26.0f);
    }

    private void SetDividerLayout(VerticalLineNode? line, float x)
    {
        if (line is null)
            return;
        line.Position = contentOrigin + new Vector2(x - 1.0f, 0.0f);
        line.Height = ContentSize.Y;
    }
}
