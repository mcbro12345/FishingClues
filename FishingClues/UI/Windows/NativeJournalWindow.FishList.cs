using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

public sealed partial class NativeJournalWindow
{
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
        var favorites = options.GuideFish?.Where(f => configuration.FavoriteFishItemIds.Contains(f.ItemId)).ToArray() ?? Array.Empty<JournalFish>();
        if (favorites.Length > 0) AddFishGroup("FAVORITES", favorites, true);
        else fishItems.Add(new FishListMessage("No favorites yet. Search for a fish and click its star to save it.", 48.0f, Wrap: true));
    }

    private void RefreshSearch()
    {
        if (fishList is null || options.GuideFish is null) return;
        ClearDetails();
        string query = searchText.Trim();
        var matches = query.Length == 0
            ? Array.Empty<JournalFish>()
            : options.GuideFish.Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        FillFishList(() =>
        {
            if (query.Length == 0) ShowSearchPrompt();
            else AddFishGroup("MATCHES", matches, true, countFirst: true);
        });
        fishList.ScrollToStart();
        if (query.Length == 0) return;
        if (requestedSearchItem != 0) {
            var match = matches.FirstOrDefault(f => f.ItemId == requestedSearchItem);
            requestedSearchItem = 0;
            if (match is not null) SelectFishRow(match.FishParameterId);
        }
        else if (sessionState.SelectedFish != 0) SelectFishRow(sessionState.SelectedFish);
    }

    private void SelectFishRow(uint fishParameterId)
    {
        var row = fishButtons.FirstOrDefault(p => p.Value == fishParameterId).Key;
        if (row is null) return;
        ToggleFishSelection(row.Fish);
        RestoreScroll(fishList, Math.Max(0, row.Y));
    }

    private readonly Dictionary<uint, int> unknownFishNumbers = new();

    // closes a hovered row's tooltip, it never sends mouse-out when removed
    private void ResetFishListState()
    {
        unknownFishNumbers.Clear();
        fishHeader?.HideTooltip();
        fishButtons.Clear();
        availabilityRows.Clear();
    }

    private void SelectSpot(JournalSpot spot, bool zoomToSpot = false, bool force = false)
    {
        if (!force && selectedSpot?.Id == spot.Id && fishList is not null)
        {
            // Already on this hole: only the map is updated, so the open fish stays selected.
            OpenAreaContaining(spot);
            if (selectedRegion?.Areas.FirstOrDefault(a => a.Name == spot.Area) is { } currentArea)
                ShowAreaMap(currentArea, spot, zoomToSpot);
            return;
        }
        ClearDetails();
        selectedSpot = spot;
        foreach (var pair in spotButtons) pair.Key.Selected = pair.Value == spot.Id;
        sessionState.SelectedRegion = selectedRegion?.Name;
        sessionState.SelectedSpot = spot.Id;
        OpenAreaContaining(spot);

        JournalArea? spotArea = selectedRegion?.Areas.FirstOrDefault(a => a.Name == spot.Area);
        if (spotArea is not null) ShowAreaMap(spotArea, spot, zoomToSpot);
        if (fishList is null)
            return;

        if (spotTitle is not null) spotTitle.String = spot.Name;
        if (spotSummary is not null)
            spotSummary.String = $"Caught {spot.CaughtCount}/{spot.Fish.Count}    |    {spot.MissingCount} remaining";
        IReadOnlyList<JournalFish> caught = spot.Fish.Where(f => f.IsCaught).ToArray();
        IReadOnlyList<JournalFish> missing = spot.Fish.Where(f => !f.IsCaught).ToArray();
        renderedUncaughtFirst = configuration.UncaughtFishFirst;
        FillFishList(() =>
        {
            if (renderedUncaughtFirst && missing.Count > 0)
                AddFishGroup("NOT CAUGHT", missing, revealNames: false);
            AddFishGroup("CAUGHT", caught, revealNames: true);
            if (!renderedUncaughtFirst && missing.Count > 0)
                AddFishGroup("NOT CAUGHT", missing, revealNames: false);
        });
        fishList.ScrollToStart();
    }

    // fish list items are matched onto existing nodes so refills reuse the rows (new nodes flash blank)
    private abstract record FishListItem;
    private sealed record FishListDivider : FishListItem;
    private sealed record FishListHeading(string Text) : FishListItem;
    private sealed record FishListMessage(string Text, float Height, bool Wrap) : FishListItem;
    private sealed record FishListRow(JournalFish Fish, string Label) : FishListItem;

    private readonly List<FishListItem> fishItems = new();

    private void AddFishGroup(string heading, IReadOnlyList<JournalFish> fish, bool revealNames, bool countFirst = false)
    {
        if (fishItems.Count > 0) fishItems.Add(new FishListDivider());
        fishItems.Add(new FishListHeading(countFirst ? $"{fish.Count} {heading}" : $"{heading}   {fish.Count}"));
        if (fish.Count == 0)
        {
            fishItems.Add(new FishListMessage("None", 23.0f, Wrap: false));
            return;
        }
        for (int i = 0; i < fish.Count; i++)
        {
            JournalFish entry = fish[i];
            bool nameShown = revealNames || entry.IdentityVisible;
            if (!nameShown) unknownFishNumbers[entry.FishParameterId] = i + 1;
            fishItems.Add(new FishListRow(entry, nameShown ? entry.Name : $"???? #{i + 1}"));
        }
    }

    private void FillFishList(Action addItems)
    {
        if (fishList is null) return;
        ResetFishListState();
        fishItems.Clear();
        addItems();
        ApplyFishItems();
    }

    private void ApplyFishItems()
    {
        if (fishList is null) return;
        ReconcileNodes(fishList.ContentNode, fishItems, FishItemMatches, CreateFishNode, UpdateFishNode);
        fishItems.Clear();
        UpdateFishSelection();
        RefreshFishListLayout();
    }
    private static bool FishItemMatches(NodeBase node, FishListItem item) => item switch
    {
        FishListDivider => node is HorizontalLineNode,
        FishListHeading => node is CategoryTextNode,
        FishListMessage => node is LabelTextNode,
        FishListRow => node is FishEntryRowNode,
        _ => false,
    };

    private NodeBase CreateFishNode(FishListItem item) => item switch
    {
        FishListDivider => new HorizontalLineNode { Height = 2.0f },
        FishListHeading => new CategoryTextNode(),
        FishListMessage => new LabelTextNode { FontSize = 14 },
        FishListRow row => new FishEntryRowNode(row.Fish, row.Label, RowClick(row.Fish)),
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    private void UpdateFishNode(NodeBase node, FishListItem item)
    {
        switch (item)
        {
            case FishListHeading heading:
                ((CategoryTextNode)node).String = heading.Text;
                break;
            case FishListMessage message:
                var label = (LabelTextNode)node;
                label.String = message.Text;
                label.Height = message.Height;
                if (message.Wrap)
                {
                    label.Width = Math.Max(80, fishList!.Width - 24);
                    label.AlignmentType = AlignmentType.Top;
                    label.TextFlags = TextFlags.WordWrap | TextFlags.MultiLine;
                }
                break;
            case FishListRow rowItem:
                var row = (FishEntryRowNode)node;
                JournalFish entry = rowItem.Fish;
                row.Rebind(entry, rowItem.Label, RowClick(entry));
                fishButtons[row] = entry.FishParameterId;
                var availability = options.GetAvailability?.Invoke(entry);
                if (availability is not null)
                {
                    row.SetAvailability(availability.AvailableNow, availability.BadgeText, availability.Tooltip);
                    availabilityRows.Add((row, entry));
                }
                else row.ClearAvailability();
                if (GuideMode) row.SetFavoriteStar(configuration.FavoriteFishItemIds.Contains(entry.ItemId), () => ToggleFavorite(entry));
                break;
        }
    }

    private Action RowClick(JournalFish entry) => () => ToggleFishSelection(entry);

    private void ToggleFishSelection(JournalFish entry)
    {
        if (selectedFish == entry.FishParameterId)
        {
            ClearDetails();
            UpdateFishSelection();
            return;
        }
        selectedFish = entry.FishParameterId;
        UpdateFishSelection();
        selectedDetails = options.BuildDetails(entry);
        if (options.BuildGuideDetails is not null)
        {
            selectedGuide = options.BuildGuideDetails(entry);
            guideLocation = selectedGuide.Locations.FirstOrDefault();
        }
        RenderDetails();
        detailsList?.ScrollToStart();
    }

    private bool ToggleFavorite(JournalFish entry)
    {
        bool favorite = configuration.FavoriteFishItemIds.Add(entry.ItemId);
        if (!favorite) configuration.FavoriteFishItemIds.Remove(entry.ItemId);
        options.SaveLayout();
        // can't rebuild the list from inside its own row's click handler
        if (string.IsNullOrWhiteSpace(searchText)) searchPending = true;
        return favorite;
    }

    private void UpdateFishSelection()
    {
        foreach (var pair in fishButtons) pair.Key.Selected = pair.Value == selectedFish;
    }
}
