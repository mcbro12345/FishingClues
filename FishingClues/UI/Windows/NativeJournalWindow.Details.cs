using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

// fish details panel, matched onto existing nodes so switching fish reuses them (new nodes draw blank for a frame)
public sealed partial class NativeJournalWindow
{
    private const float TightLineHeight = 18.0f;
    private const float HeadingHeight = 24.0f;
    private const uint HeadingFontSize = 16;

    private abstract record DetailItem;
    private sealed record DetailLabel(string Text, uint FontSize, FontType? Font) : DetailItem;
    private sealed record DetailRow(string Text, uint FontSize, FontType? Font) : DetailItem;
    private sealed record DetailSelector : DetailItem;
    private sealed record DetailBody : DetailItem;

    // panel items and the body under the name (refilled alone when the guide location changes)
    private readonly List<DetailItem> detailItems = new();
    private readonly List<DetailItem> bodyItems = new();
    private JournalListNode? catchBody;
    private bool writingCatchBody;

    private bool DetailsOpen => selectedDetails is not null;

    private void ClearDetails()
    {
        HidePartialHoverTooltip();
        catchBody = null;
        writingCatchBody = false;
        selectedDetails = null;
        selectedGuide = null;
        guideLocation = null;
        guideDetailsPending = false;
        selectedFish = 0;
        detailsList?.ContentNode.Clear();
        detailsList?.RecalculateSizes();
        UpdateDetailsHint();
        SyncDetailsLayout();
    }

    private void RenderDetails()
    {
        if (detailsList is null) return;
        detailItems.Clear();
        bodyItems.Clear();
        detailsList.ContentNode.FirstItemSpacing = 0.0f;
        if (selectedGuide is not null)
        {
            // the name row is always link-style so known and unknown fish share the same nodes
            AddDetailLine(UnknownFishName(selectedGuide.Name), HeadingFontSize, FontType.Jupiter, forceRow: true);
            if (GuideMode) detailItems.Add(new DetailSelector());
            detailItems.Add(new DetailBody());
            BuildBodyItems();
        }
        else if (selectedDetails is { } section)
        {
            AddDetailLine(UnknownFishName(section.Heading));
            foreach (string line in section.Lines) AddDetailLine(line);
        }
        ApplyDetailItems(detailsList.ContentNode, detailItems);
        catchBody = detailsList.ContentNode.GetNodes<JournalListNode>().FirstOrDefault();
        ApplyBodyItems();
        detailsList.RecalculateSizes();
        UpdateDetailsHint();
        SyncDetailsLayout();
    }

    private void RenderCatchBody()
    {
        if (catchBody is null || selectedGuide is null) return;
        bodyItems.Clear();
        BuildBodyItems();
        ApplyBodyItems();
        detailsList?.RecalculateSizes();
    }

    private void BuildBodyItems()
    {
        if (selectedGuide is null) return;
        writingCatchBody = true;
        if (guideLocation is not null)
            foreach (string line in selectedGuide.GetDetails(guideLocation)) AddDetailLine(line);
        else AddDetailLine("Location requirements unknown.");
        if (selectedGuide.Info.Count > 0)
        {
            AddDetailLine("Description:");
            foreach (string line in selectedGuide.Info) AddDetailLine(line);
        }
        writingCatchBody = false;
    }

    private void ApplyBodyItems()
    {
        if (catchBody is null) return;
        ApplyDetailItems(catchBody, bodyItems);
        catchBody.RecalculateLayout();
    }

    private void AddDetailLine(string text, uint fontSize = 14, FontType? fontType = null, bool forceRow = false)
    {
        bool isItemRow = selectedGuide is not null
            && (forceRow || text.Contains(':') && !selectedGuide.Info.Contains(text) || selectedGuide.ItemLinks.ContainsKey(text));
        DetailItem item = isItemRow ? new DetailRow(text, fontSize, fontType) : new DetailLabel(text, fontSize, fontType);
        (writingCatchBody ? bodyItems : detailItems).Add(item);
    }

    private void ApplyDetailItems(JournalListNode list, List<DetailItem> items)
        => ReconcileNodes(list, items, DetailItemMatches, CreateDetailNode, UpdateDetailNode);

    // makes the list's nodes line up with items, reusing a node while its shape still fits
    // and dropping it and everything after it once it stops fitting
    private static void ReconcileNodes<T>(LayoutListNode list, IReadOnlyList<T> items,
        Func<NodeBase, T, bool> matches, Func<T, NodeBase> create, Action<NodeBase, T> update)
    {
        var existing = list.Nodes.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            if (i < existing.Count && !matches(existing[i], items[i]))
            {
                for (int j = existing.Count - 1; j >= i; j--) list.RemoveNode(existing[j]);
                existing.RemoveRange(i, existing.Count - i);
            }
            NodeBase node;
            if (i < existing.Count) node = existing[i];
            else
            {
                node = create(items[i]);
                list.AddNode(node);
                existing.Add(node);
            }
            update(node, items[i]);
        }
        for (int j = existing.Count - 1; j >= items.Count; j--) list.RemoveNode(existing[j]);
    }
    private static bool DetailItemMatches(NodeBase node, DetailItem item) => item switch
    {
        // a heading and a plain line are laid out differently, so they never share a node
        DetailLabel label => node is LabelTextNode existing && (existing.FontSize > 14) == (label.FontSize > 14),
        DetailRow => node is ItemDetailRow,
        DetailSelector => node is DetailSelectorRow<GuideLocation>,
        DetailBody => node is JournalListNode,
        _ => false,
    };

    private NodeBase CreateDetailNode(DetailItem item) => item switch
    {
        DetailLabel => new LabelTextNode { TextFlags = TextFlags.WordWrap | TextFlags.MultiLine },
        DetailRow row => new ItemDetailRow(row.Text, selectedGuide!.ItemLinks, row.FontSize, row.Font),
        DetailSelector => new DetailSelectorRow<GuideLocation>(
            selectedGuide!.Locations, guideLocation, l => l.Label,
            l => { guideLocation = l; guideDetailsPending = true; }, "Unknown"),
        DetailBody => new JournalListNode { FitContents = true },
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

    private void UpdateDetailNode(NodeBase node, DetailItem item)
    {
        float width = Math.Max(80.0f, detailsList!.Width - 24.0f);
        switch (item)
        {
            case DetailLabel labelItem:
                var label = (LabelTextNode)node;
                label.Width = width;
                label.FontSize = labelItem.FontSize;
                // wrapped lines sit closer together than the default
                label.LineSpacing = 18 + (labelItem.FontSize - 14) * 3 / 2;
                if (labelItem.Font is FontType font) label.FontType = font;
                // bottom-aligned in a fixed box so tall glyphs aren't clipped
                if (labelItem.FontSize > 14) label.AlignmentType = AlignmentType.BottomLeft;
                label.String = labelItem.Text;
                label.Height = LineHeight(label);
                break;
            case DetailRow rowItem:
                var row = (ItemDetailRow)node;
                row.SetContent(rowItem.Text, selectedGuide!.ItemLinks, rowItem.FontSize, rowItem.Font);
                row.Width = width;
                break;
            case DetailSelector:
                var selector = (DetailSelectorRow<GuideLocation>)node;
                selector.SetOptions(selectedGuide!.Locations, guideLocation);
                selector.Width = width;
                break;
            case DetailBody:
                node.Width = width;
                break;
        }
    }

    // open/close as soon as the selection changes so new contents never show in a panel of the old size
    private void SyncDetailsLayout()
    {
        if (DetailsOpen != detailsLaidOutOpen) LayoutAttachedNodes();
    }

    // undiscovered fish are "Unknown Fish N" like their list entry (the heading font draws # as a numero sign)
    private string UnknownFishName(string name)
        => !GuideMode && unknownFishNumbers.TryGetValue(selectedFish, out int number) ? $"Unknown Fish {number}" : name;

    private void ReflowDetails()
    {
        if (detailsList is null) return;
        float width = Math.Max(80.0f, detailsList.Width - 24.0f);
        foreach (var label in detailsList.ContentNode.GetNodes<LabelTextNode>())
        {
            if (Math.Abs(label.Width - width) < 1) continue;
            label.Width = width;
            label.Height = LineHeight(label);
        }
        foreach (var row in detailsList.ContentNode.GetNodes<ItemDetailRow>()) row.Width = width;
        foreach (var row in detailsList.ContentNode.GetNodes<DetailSelectorRow<GuideLocation>>()) row.Width = width;
        if (catchBody is not null && Math.Abs(catchBody.Width - width) >= 1) {
            catchBody.Width = width;
            foreach (var label in catchBody.GetNodes<LabelTextNode>()) {
                label.Width = width;
                label.Height = LineHeight(label);
            }
            foreach (var row in catchBody.GetNodes<ItemDetailRow>()) row.Width = width;
            catchBody.RecalculateLayout();
        }
        detailsList.RecalculateSizes();
        UpdateDetailsHint();
    }

    private void UpdateDetailsHint()
    {
        if (detailsHint is null || detailsList is null) return;
        detailsHint.Position = detailsList.Position;
        detailsHint.Size = detailsList.Size;
        detailsHint.IsVisible = selectedDetails is null;
    }

    private static float LineHeight(LabelTextNode label)
        => label.FontSize > 14 ? HeadingHeight : Math.Max(TightLineHeight, label.GetTextDrawSize(false).Y + 2.0f);
}
