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

using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

public sealed partial class NativeJournalWindow
{
    public void SubmitSearch(string query, uint itemId = 0)
    {
        requestedSearchItem = itemId;
        searchText = query;
        sessionState.SearchText = query;
        if (searchInput is not null) searchInput.String = query;
        searchPending = true;
    }

    private void ShowSearchPrompt()
    {
        if (fishList is null) return;
        var favorites = guideFish?.Where(f => configuration.FavoriteFishItemIds.Contains(f.ItemId)).ToArray() ?? Array.Empty<JournalFish>();
        if (favorites.Length > 0) AddFishGroup("FAVORITES", favorites, true);
        else fishList.ContentNode.AddNode(new LabelTextNode {
            Height = 48, FontSize = 14, Width = Math.Max(80, fishList.Width - 24),
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            String = "No favorites yet. Search for a fish and click its star to save it."
        });
        RefreshFishListLayout();
    }

    private void RefreshSearch()
    {
        if (fishList is null || guideFish is null) return;
        ClearDetails();
        ClearFishList();
        if (string.IsNullOrWhiteSpace(searchText)) {
            ShowSearchPrompt(); fishList.ScrollToStart(); return;
        }
        var matches = guideFish.Where(f => f.Name.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        AddFishGroup($"FISH - {matches.Length} matches", matches, true);
        RefreshFishListLayout();
        fishList.ScrollToStart();
        if (requestedSearchItem != 0) {
            var match = matches.FirstOrDefault(f => f.ItemId == requestedSearchItem);
            requestedSearchItem = 0;
            if (match is not null) {
                var row = fishButtons.FirstOrDefault(p => p.Value == match.FishParameterId).Key;
                row?.OnClick?.Invoke();
                if (row is not null) fishList.ScrollBarNode.ScrollPosition = Math.Max(0, row.Y);
            }
        }
        else if (sessionState.SelectedFish != 0) {
            var row = fishButtons.FirstOrDefault(p => p.Value == sessionState.SelectedFish).Key;
            row?.OnClick?.Invoke();
            if (row is not null) fishList.ScrollBarNode.ScrollPosition = Math.Max(0, row.Y);
        }
    }

    private void SelectRegion(JournalRegion region, bool restoring = false)
    {
        if (!restoring && selectedRegion is not null) SaveViewState();
        sessionState.Regions.TryGetValue(region.Name, out var remembered);
        if (remembered is not null) {
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

        spotButtons.Clear();
        areaList.ContentNode.Clear();
        var areaHeaders = new List<CollapsingHeaderNode>();
        for (int areaIndex = 0; areaIndex < region.Areas.Count; areaIndex++)
        {
            JournalArea area = region.Areas[areaIndex];
            string areaKey = $"{region.Name}\n{area.Name}";
            var areaDropDown = new AnimatedAreaHeaderNode
            {
                String = region.IsUnlocked ? area.Name : "???",
                Width = EffectiveDropdownWidth(),
                FitWidth = true,
                ItemSpacing = 2.0f,
                FirstItemSpacing = 1.0f,
                IsCollapsed = !sessionState.ExpandedAreas.Contains(areaKey) && !area.Spots.Any(s => s.Id == sessionState.SelectedSpot),
            };
            areaDropDown.OnToggle = expanded =>
            {
                if (!expanded && selectedSpot is not null && area.Spots.Any(s => s.Id == selectedSpot.Id)) {
                    areaDropDown.RestoreExpandedOnNextTick = true;
                    return;
                }
                if (expanded)
                {
                    sessionState.ExpandedAreas.Add(areaKey);
                }
                else
                {
                    sessionState.ExpandedAreas.Remove(areaKey);
                }
                foreach (var header in areaHeaders.OfType<AnimatedAreaHeaderNode>()) header.RecalculateLayout();
                areaList?.RecalculateSizes();
                // Toggling can add/remove the scrollbar, which changes the column width.
                ApplyAreaDropdownWidths();
            };
            areaHeaders.Add(areaDropDown);
            foreach (JournalSpot spot in area.Spots)
            {
                JournalSpot captured = spot;
                if (spot.IsUnlocked)
                {
                    var spotButton = new ListButtonNode
                    {
                        Height = 25.0f,
                        String = $"  {spot.Name}",
                        OnClick = () => SelectSpot(captured),
                    };
                    spotButtons.Add(spotButton, spot.Id);
                    areaDropDown.AddNode(spotButton);
                }
                else
                {
                    areaDropDown.AddNode(new LabelTextNode
                    {
                        Height = 25.0f,
                        FontSize = 14,
                        String = "  Undiscovered",
                    });
                }
            }
            areaDropDown.InitializeAnimation();
            areaList.ContentNode.AddNode(areaDropDown);
        }
        areaList.RecalculateSizes();
        ApplyAreaDropdownWidths();
        ClearFishList();
        RefreshFishListLayout();

        JournalSpot? restoredSpot = region.Areas.SelectMany(a => a.Spots)
            .FirstOrDefault(spot => spot.IsUnlocked && spot.Id == sessionState.SelectedSpot);
        if (restoredSpot is not null) {
            SelectSpot(restoredSpot);
            var restoredRow = fishButtons.FirstOrDefault(p => p.Value == sessionState.SelectedFish).Key;
            restoredRow?.OnClick?.Invoke();
        }
        if (remembered is not null) {
            RestoreScroll(areaList, remembered.AreaScroll);
            RestoreScroll(fishList, remembered.FishScroll);
            if (detailsList is not null) RestoreScroll(detailsList, remembered.DetailsScroll);
        }
    }

    // RecalculateSizes() alone leaves rows measuring against a stale column
    // width, so FitWidth rows (the availability badges) need RecalculateLayout too.
    private void RefreshFishListLayout()
    {
        fishList?.RecalculateSizes();
        fishList?.ContentNode.RecalculateLayout();
    }

    // A row destroyed while hovered never fires its own MouseOut, so its
    // tooltip needs closing here; a row left in availabilityRows after being
    // destroyed would get SetAvailability called on it by the 30s refresh.
    private void ClearFishList()
    {
        fishHeader?.HideTooltip();
        fishButtons.Clear();
        availabilityRows.Clear();
        fishList?.ContentNode.Clear();
    }

    private void SelectSpot(JournalSpot spot)
    {
        ClearDetails();
        selectedSpot = spot;
        foreach (var pair in spotButtons) pair.Key.Selected = pair.Value == spot.Id;
        sessionState.SelectedRegion = selectedRegion?.Name;
        sessionState.SelectedSpot = spot.Id;
        if (selectedRegion is not null) sessionState.ExpandedAreas.Add($"{selectedRegion.Name}\n{spot.Area}");
        if (fishList is null)
            return;

        ClearFishList();
        if (spotTitle is not null) spotTitle.String = spot.Name;
        if (spotSummary is not null)
            spotSummary.String = $"Caught {spot.CaughtCount}/{spot.Fish.Count}    |    {spot.MissingCount} remaining";
        IReadOnlyList<JournalFish> caught = spot.Fish.Where(f => f.IsCaught).ToArray();
        IReadOnlyList<JournalFish> missing = spot.Fish.Where(f => !f.IsCaught).ToArray();
        renderedUncaughtFirst = configuration.UncaughtFishFirst;
        if (renderedUncaughtFirst && missing.Count > 0)
            AddFishGroup("NOT CAUGHT", missing, revealNames: false);
        AddFishGroup("CAUGHT", caught, revealNames: true);
        if (!renderedUncaughtFirst && missing.Count > 0)
            AddFishGroup("NOT CAUGHT", missing, revealNames: false);
        RefreshFishListLayout();
        fishList.ScrollToStart();
    }

    private void AddFishGroup(string heading, IReadOnlyList<JournalFish> fish, bool revealNames)
    {
        if (fishList is null)
            return;
        if (fishList.ContentNode.Nodes.Count > 0)
            fishList.ContentNode.AddNode(new HorizontalLineNode { Height = 2.0f });
        fishList.ContentNode.AddNode(new CategoryTextNode { String = $"{heading}   {fish.Count}" });
        if (fish.Count == 0)
        {
            fishList.ContentNode.AddNode(new LabelTextNode
            {
                Height = 23.0f,
                FontSize = 14,
                String = "None",
            });
            return;
        }

        for (int i = 0; i < fish.Count; i++)
        {
            JournalFish entry = fish[i];
            string label = revealNames || entry.IdentityVisible ? entry.Name : $"???? #{i + 1}";
            string rowLabel = entry.Level > 0 ? $"{label}   Lv. {entry.Level}" : label;
            var row = new FishEntryRowNode(entry, rowLabel, () =>
            {
                selectedFish = entry.FishParameterId;
                UpdateFishSelection();
                selectedDetails = buildDetails(entry);
                if (guideDetails is not null) {
                    selectedGuide = guideDetails(entry);
                    guideLocation = selectedGuide.Locations.FirstOrDefault();
                    guidePoles = guideLocation is null ? Array.Empty<FishingPole>() : selectedGuide.GetPoles(guideLocation);
                    guidePole = guidePoles.FirstOrDefault();
                }
                RenderDetails();
                detailsList?.ScrollToStart();
            });
            fishButtons.Add(row, entry.FishParameterId);
            var availability = getAvailability?.Invoke(entry);
            if (availability is not null)
            {
                row.SetAvailability(availability.AvailableNow, availability.BadgeText, availability.Tooltip);
                availabilityRows.Add((row, entry));
            }
            if (GuideMode) row.AddFavoriteStar(configuration.FavoriteFishItemIds.Contains(entry.ItemId), () => {
                bool favorite = configuration.FavoriteFishItemIds.Add(entry.ItemId);
                if (!favorite) configuration.FavoriteFishItemIds.Remove(entry.ItemId);
                saveDivider();
                // can't rebuild the list from inside its own row's click handler
                if (string.IsNullOrWhiteSpace(searchText)) searchPending = true;
                return favorite;
            });
            fishList.ContentNode.AddNode(row);
        }
    }

    private void UpdateFishSelection()
    {
        foreach (var pair in fishButtons) pair.Key.Selected = pair.Value == selectedFish;
    }
}
