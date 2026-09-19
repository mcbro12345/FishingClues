using System;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// Where everything sits: the column widths, the layout pass, and the area dropdowns' widths.
public sealed partial class NativeJournalWindow
{
    // The positions and sizes of the details panel's parts, for the diagnostics report.
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

    // The region and area column widths after clamping to the window.
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

        // Laying the lists out again resets their scroll, so keep it.
        float regionScroll = regionList?.ScrollBarNode.ScrollPosition ?? 0;
        float areaScroll = areaList?.ScrollBarNode.ScrollPosition ?? 0;
        float fishScroll = fishList?.ScrollBarNode.ScrollPosition ?? 0;
        float detailsScroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;

        SetWindowSize(Size);
        LayoutAttachedNodes();
        RefreshSelectedDetails();

        if (regionList is not null) RestoreScroll(regionList, regionScroll);
        if (areaList is not null) RestoreScroll(areaList, areaScroll);
        if (fishList is not null) RestoreScroll(fishList, fishScroll);
        if (detailsList is not null) RestoreScroll(detailsList, detailsScroll);
    }

    // Re-renders the open fish's details so settings like the time format apply to it.
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
            // In the fish guide the same divider separates the search bar from the results.
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
            // The button fills the rest of the row, right up against the input.
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
            // With no fish selected the panel shrinks to a strip holding its hint. The
            // saved height ratio is untouched, so the panel reopens at the same size.
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
            // The game applies a node's new size at once but only redraws it at its new
            // position a frame later (its diagnostics report showed the on-screen Y
            // trailing the set Y by exactly one frame). While dragging the details
            // divider the moving nodes' sizes therefore use last frame's values, so
            // each list's edge stays put against its divider instead of running ahead.
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
            // The list runs from just under the top divider to just above the bottom
            // one, so scrolled text is cut off flush at the divider lines.
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

    // The inset settings are relative to a preferred baseline (see Configuration), and
    // can be negative to let a dropdown extend past the edge of the area column.
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
        // RecalculateSizes lays the content out again, which resets every header's X, so the inset is applied after.
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
