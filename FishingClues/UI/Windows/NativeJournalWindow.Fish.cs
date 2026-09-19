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

using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

public sealed partial class NativeJournalWindow
{
    // The generic UI click sound (see FishEntryRowNode.AddFavoriteStar's own
    // use of the same id) - used here for area headers opening, which
    // otherwise happen completely silently (see this file's own OnToggle
    // wiring for why).
    private const uint UiClickSoundEffectId = 1;

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

    // forceOpenArea/forceSelectSpot let a caller override the normal
    // persisted-view lookup below - used for the very first open of a
    // session (auto-navigate to the player's current location) and for a
    // hole that was just discovered since the log was last opened.
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

        // Exactly one area is ever open at a time: whichever holds the hole
        // we're about to select (forced, remembered, or newly picked up
        // below), falling back to the first area for a region with nothing
        // else to go on yet (a region visited for the first time this
        // session, or with no persisted spot in it).
        JournalArea? openArea = forceOpenArea
            ?? region.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == sessionState.SelectedSpot))
            ?? region.Areas.FirstOrDefault();

        spotButtons.Clear();
        areaList.ContentNode.Clear();
        areaHeaderByArea.Clear();
        var areaHeaders = new List<AnimatedAreaHeaderNode>();
        foreach (JournalArea area in region.Areas)
        {
            JournalArea capturedArea = area;
            var areaDropDown = new AnimatedAreaHeaderNode
            {
                String = region.IsUnlocked ? area.Name : "???",
                Width = EffectiveDropdownWidth(),
                FitWidth = true,
                ItemSpacing = 2.0f,
                FirstItemSpacing = 1.0f,
                IsCollapsed = !ReferenceEquals(area, openArea),
            };
            // FontType.Miedinger was tried here for a "header" look to match
            // the region list, but it's a numeric/header-only glyph set in
            // this game with no lowercase letters - real area names rendered
            // as dashes for every unsupported character. Left on the normal
            // Axis font (the header's default) so names actually display.
            areaDropDown.OnToggle = expanded =>
            {
                if (!expanded)
                {
                    if (areaDropDown.AllowProgrammaticCollapse) { areaDropDown.AllowProgrammaticCollapse = false; return; }
                    // Exactly one area must always stay open - deny the user
                    // collapsing whichever one that currently is.
                    areaDropDown.RestoreExpandedOnNextTick = true;
                    return;
                }
                // Opening this one closes every other area in this region -
                // only ever one dropdown open at a time. Each other header's
                // own OnToggle(false) fires from this, and lets it through
                // because AllowProgrammaticCollapse is set first.
                foreach (var otherHeader in areaHeaders)
                {
                    if (ReferenceEquals(otherHeader, areaDropDown) || otherHeader.IsCollapsed) continue;
                    otherHeader.AllowProgrammaticCollapse = true;
                    otherHeader.IsCollapsed = true;
                }
                foreach (var header in areaHeaders) header.RecalculateLayout();
                areaList?.RecalculateSizes();
                // Toggling can add/remove the scrollbar, which changes the column width.
                ApplyAreaDropdownWidths();
                // Area headers otherwise open completely silently (see
                // ToggleableHeaderNode - unlike a real button component, it
                // never plays a sound on its own) - this is the same generic
                // click sound FishEntryRowNode's favorite star uses. Fires
                // for every way an area gets opened (a direct title click,
                // and the partial-click-forward override in
                // NativeJournalWindow.Draw.cs, which sets IsCollapsed the
                // same way), but not the collapse side, matching what was
                // asked for.
                unsafe { UIGlobals.PlaySoundEffect(UiClickSoundEffectId); }
            };
            areaHeaders.Add(areaDropDown);
            areaHeaderByArea[capturedArea] = areaDropDown;
            foreach (JournalSpot spot in area.Spots)
            {
                JournalSpot captured = spot;
                if (spot.IsUnlocked)
                {
                    var spotButton = new ListButtonNode
                    {
                        Height = 25.0f,
                        String = $"  {spot.Name}",
                        OnClick = () => SelectSpot(captured, zoomToSpot: true),
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
        // No fallback ShowAreaMap call here when there's no remembered hole
        // to restore - the map (and its area name/caption) stays exactly as
        // it was until a fishing hole is actually clicked, so switching
        // regions/areas alone never changes what's shown or refreshes it
        // for an area that has nothing discovered yet. Once a hole HAS ever
        // been picked this window (mapArea is no longer null) that's the
        // only thing that governs the map from here on; before that, the
        // generic preview backdrop follows along to whatever area this
        // region just opened to instead of sitting on the previous region's.
        if (mapArea is null) RefreshMapPreview(openArea);
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
    // The number each undiscovered fish carries in the list ("???? #2"), so its
    // details heading can read "Unknown fish #2".
    private readonly Dictionary<uint, int> unknownFishNumbers = new();

    private void ClearFishList()
    {
        unknownFishNumbers.Clear();
        fishHeader?.HideTooltip();
        fishButtons.Clear();
        availabilityRows.Clear();
        fishList?.ContentNode.Clear();
    }

    private void SelectSpot(JournalSpot spot, bool zoomToSpot = false)
    {
        ClearDetails();
        selectedSpot = spot;
        foreach (var pair in spotButtons) pair.Key.Selected = pair.Value == spot.Id;
        sessionState.SelectedRegion = selectedRegion?.Name;
        sessionState.SelectedSpot = spot.Id;
        // Selecting a hole in a different area than the one currently open
        // (e.g. via a map marker click) switches which area dropdown is
        // open - the accordion invariant (only one open) is self-enforcing
        // once opened, since opening one collapses the rest (see SelectRegion).
        OpenAreaContaining(spot);
        // Clicking a fishing hole is the one action that's allowed to change
        // the map - it always jumps to and centers on the hole just clicked.
        JournalArea? spotArea = selectedRegion?.Areas.FirstOrDefault(a => a.Name == spot.Area);
        if (spotArea is not null) ShowAreaMap(spotArea, spot, zoomToSpot);
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

    // Live-switches which area's dropdown is open to whichever one holds the
    // given spot, if that area isn't already the open one. Opening it fires
    // its own OnToggle(true), which collapses every other area for us (see
    // the accordion logic built in SelectRegion) - this just has to open it.
    private void OpenAreaContaining(JournalSpot spot)
    {
        JournalArea? area = selectedRegion?.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == spot.Id));
        if (area is not null && areaHeaderByArea.TryGetValue(area, out var header) && header.IsCollapsed)
            header.IsCollapsed = false;
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
            if (!(revealNames || entry.IdentityVisible)) unknownFishNumbers[entry.FishParameterId] = i + 1;
            var row = new FishEntryRowNode(entry, label, () =>
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
