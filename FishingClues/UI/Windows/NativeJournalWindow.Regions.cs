using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

// Picking a region and filling the area list with its areas and fishing holes.
public sealed partial class NativeJournalWindow
{
    // forceOpenArea and forceSelectSpot override the remembered view: used to open at the player's
    // location and at a newly discovered hole.
    private void SelectRegion(JournalRegion region, bool restoring = false, JournalArea? forceOpenArea = null, JournalSpot? forceSelectSpot = null)
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

        // One area is open at a time: the one holding the hole about to be selected, else the first.
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
            SelectSpot(restoredSpot);
            var restoredRow = fishButtons.FirstOrDefault(p => p.Value == sessionState.SelectedFish).Key;
            if (restoredRow is not null) ToggleFishSelection(restoredRow.Fish);
        }
        else
        {
            fishList.ContentNode.Clear();
            RefreshFishListLayout();
        }
        // Until a hole has been picked the map is only a generic backdrop; after that only picking a hole changes it.
        if (mapArea is null) RefreshMapPreview(openArea);
        if (remembered is not null) {
            RestoreScroll(areaList, remembered.AreaScroll);
            RestoreFishScroll(remembered.FishScroll);
            RestoreDetailsScroll(remembered.DetailsScroll);
        }
    }

    // Fills the area list with the region's areas and holes. Headers and buttons already
    // on screen are reused rather than replaced, since new nodes draw blank for a frame.
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

    // Matches the header's rows to the area's holes.
    private void FillAreaHeader(AnimatedAreaHeaderNode header, JournalArea area)
    {
        var existing = header.Nodes.ToList();
        for (int i = 0; i < area.Spots.Count; i++)
        {
            JournalSpot spot = area.Spots[i];
            bool sameKind = i < existing.Count && (spot.IsUnlocked ? existing[i] is ListButtonNode : existing[i] is LabelTextNode);
            if (i < existing.Count && !sameKind)
            {
                for (int j = existing.Count - 1; j >= i; j--) header.RemoveNode(existing[j]);
                existing.RemoveRange(i, existing.Count - i);
            }
            NodeBase node;
            if (i < existing.Count) node = existing[i];
            else
            {
                node = spot.IsUnlocked
                    ? new ListButtonNode { Height = 25.0f }
                    : new LabelTextNode { Height = 25.0f, FontSize = 14 };
                header.AddNode(node);
                existing.Add(node);
            }
            if (node is ListButtonNode button)
            {
                button.String = $"  {spot.Name}";
                button.Selected = false;
                button.OnClick = () => SelectSpot(spot, zoomToSpot: true);
                spotButtons[button] = spot.Id;
            }
            else if (node is LabelTextNode label) label.String = "  Undiscovered";
        }
        for (int j = existing.Count - 1; j >= area.Spots.Count; j--) header.RemoveNode(existing[j]);
    }

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
        unsafe { UIGlobals.PlaySoundEffect(UiClickSoundEffectId); }
    }

    // RecalculateSizes() alone leaves rows measuring against a stale column
    // width, so FitWidth rows (the availability badges) need RecalculateLayout too.
    private void RefreshFishListLayout()
    {
        fishList?.RecalculateSizes();
        fishList?.ContentNode.RecalculateLayout();
    }

    // Opens the dropdown of the area holding the spot; that collapses the others (see SelectRegion).
    private void OpenAreaContaining(JournalSpot spot)
    {
        JournalArea? area = selectedRegion?.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == spot.Id));
        if (area is not null && areaHeaderByArea.TryGetValue(area, out var header) && header.IsCollapsed)
            header.IsCollapsed = false;
    }
}
