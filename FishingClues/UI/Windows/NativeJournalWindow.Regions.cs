using System.Collections.Generic;
using System.Linq;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

// Picking a region and filling the area list with its areas and fishing holes.
public sealed partial class NativeJournalWindow
{
    // force* override the remembered view (player location / newly discovered hole), zoomToForcedSpot also zooms the map there
    private void SelectRegion(JournalRegion region, bool restoring = false, JournalArea? forceOpenArea = null,
        JournalSpot? forceSelectSpot = null, bool zoomToForcedSpot = false)
    {
        if (!restoring && selectedRegion is not null) SaveViewState();
        sessionState.Regions.TryGetValue(region.Name, out var remembered);
        if (forceSelectSpot is not null) {
            sessionState.SelectedSpot = forceSelectSpot.Id;
            sessionState.SelectedFish = 0;
        } else if (remembered is not null) {
            sessionState.SelectedSpot = remembered.Spot;
            sessionState.SelectedFish = remembered.Fish;
        } else if (!restoring) {
            sessionState.SelectedSpot = 0;
            sessionState.SelectedFish = 0;
        }
        selectedRegion = region;
        foreach (var pair in regionButtons) pair.Key.Selected = ReferenceEquals(pair.Value, region);
        selectedSpot = null;
        if (spotTitle is not null) spotTitle.String = "Select a fishing hole.";
        if (spotSummary is not null) spotSummary.String = "";
        ClearDetails();
        sessionState.SelectedRegion = region.Name;
        if (areaList is null || fishList is null)
            return;

        // one area open at a time
        JournalArea? openArea = forceOpenArea
            ?? region.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == sessionState.SelectedSpot))
            ?? region.Areas.FirstOrDefault();

        PopulateAreaList(region, openArea);
        areaList.RecalculateSizes();
        ApplyAreaDropdownWidths();
        ResetFishListState();

        JournalSpot? restoredSpot = region.Areas.SelectMany(a => a.Spots)
            .FirstOrDefault(spot => spot.IsUnlocked && spot.Id == sessionState.SelectedSpot);
        if (restoredSpot is not null) {
            SelectSpot(restoredSpot, zoomToSpot: zoomToForcedSpot);
            var restoredRow = fishButtons.FirstOrDefault(p => p.Value == sessionState.SelectedFish).Key;
            if (restoredRow is not null) ToggleFishSelection(restoredRow.Fish);
        }
        else
        {
            fishList.ContentNode.Clear();
            RefreshFishListLayout();
        }
        // the map stays a generic backdrop until a hole is picked
        if (mapArea is null) RefreshMapPreview(openArea);
        if (remembered is not null) {
            RestoreScroll(areaList, remembered.AreaScroll);
            RestoreScroll(fishList, remembered.FishScroll);
            RestoreScroll(detailsList, remembered.DetailsScroll);
        }
    }

    private void PopulateAreaList(JournalRegion region, JournalArea? openArea)
    {
        if (areaList is null) return;
        var content = areaList.ContentNode;
        var headers = content.GetNodes<AnimatedAreaHeaderNode>().ToList();
        for (int j = headers.Count - 1; j >= region.Areas.Count; j--) content.RemoveNode(headers[j]);
        if (headers.Count > region.Areas.Count) headers.RemoveRange(region.Areas.Count, headers.Count - region.Areas.Count);

        spotButtons.Clear();
        areaHeaderByArea.Clear();
        var active = new List<AnimatedAreaHeaderNode>();
        for (int i = 0; i < region.Areas.Count; i++)
        {
            JournalArea area = region.Areas[i];
            bool isNew = i >= headers.Count;
            var header = isNew
                ? new AnimatedAreaHeaderNode { FitWidth = true, ItemSpacing = 2.0f, FirstItemSpacing = 1.0f }
                : headers[i];
            // Changing IsCollapsed calls OnToggle, which must not run for this.
            header.OnToggle = null;
            header.String = region.IsUnlocked ? area.Name : "???";
            header.Width = EffectiveDropdownWidth();
            header.IsCollapsed = !ReferenceEquals(area, openArea);
            FillAreaHeader(header, area);
            header.InitializeAnimation();
            header.OnToggle = expanded => OnAreaToggled(header, expanded, active);
            if (isNew) content.AddNode(header);
            active.Add(header);
            areaHeaderByArea[area] = header;
        }
        areaList.RecalculateSizes();
    }

    private void FillAreaHeader(AnimatedAreaHeaderNode header, JournalArea area)
        => ReconcileNodes(header, area.Spots,
            (node, spot) => spot.IsUnlocked ? node is ListButtonNode : node is LabelTextNode,
            spot => spot.IsUnlocked ? new ListButtonNode { Height = 25.0f } : new LabelTextNode { Height = 25.0f, FontSize = 14 },
            (node, spot) =>
            {
                if (node is ListButtonNode button)
                {
                    button.String = $"  {spot.Name}";
                    button.Selected = false;
                    button.OnClick = () => SelectSpot(spot, zoomToSpot: true);
                    spotButtons[button] = spot.Id;
                }
                else if (node is LabelTextNode label) label.String = "  Undiscovered";
            });
    private void OnAreaToggled(AnimatedAreaHeaderNode header, bool expanded, List<AnimatedAreaHeaderNode> headers)
    {
        if (!expanded)
        {
            if (header.AllowProgrammaticCollapse) { header.AllowProgrammaticCollapse = false; return; }
            // one area must stay open, so collapsing it is undone
            header.RestoreExpandedOnNextTick = true;
            return;
        }
        // Opening one closes the others; AllowProgrammaticCollapse lets their own OnToggle(false) through.
        foreach (var other in headers)
        {
            if (ReferenceEquals(other, header) || other.IsCollapsed) continue;
            other.AllowProgrammaticCollapse = true;
            other.IsCollapsed = true;
        }
        foreach (var each in headers) each.RecalculateLayout();
        areaList?.RecalculateSizes();
        // Toggling can add/remove the scrollbar, which changes the column width.
        ApplyAreaDropdownWidths();
        // Area headers open silently on their own, so play the click sound (not when collapsing).
        PlayClickSound();
    }

    // RecalculateSizes alone leaves FitWidth rows stale, RecalculateLayout too
    private void RefreshFishListLayout()
    {
        fishList?.RecalculateSizes();
        fishList?.ContentNode.RecalculateLayout();
    }

    private void OpenAreaContaining(JournalSpot spot)
    {
        JournalArea? area = selectedRegion?.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == spot.Id));
        if (area is not null && areaHeaderByArea.TryGetValue(area, out var header) && header.IsCollapsed)
            header.IsCollapsed = false;
    }
}
