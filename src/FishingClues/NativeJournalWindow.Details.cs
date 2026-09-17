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
    private JournalListNode? catchBody;
    private DetailSelectorRow<FishingPole>? poleSelector;
    private bool writingCatchBody;

    private void ClearDetails()
    {
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HideTooltip();
        partialHover = null;
        catchBody = null;
        poleSelector = null;
        writingCatchBody = false;
        selectedDetails = null;
        selectedGuide = null;
        guideLocation = null;
        guidePole = null;
        guideDetailsPending = guideLocationPending = false;
        selectedFish = 0;
        detailsList?.ContentNode.Clear();
        detailsList?.RecalculateSizes();
        UpdateDetailsHint();
    }

    private void RenderDetails()
    {
        if (detailsList is null) return;
        catchBody = null;
        poleSelector = null;
        detailsList.ContentNode.Clear();
        if (selectedGuide is not null) {
            RenderGuideDetails();
        }
        else if (selectedDetails is { } section)
        {
            AddDetailLine(section.Heading);
            foreach (string line in section.Lines) AddDetailLine(line);
        }
        detailsList.RecalculateSizes();
        UpdateDetailsHint();
    }

    private void RenderGuideDetails()
    {
        if (detailsList is null || selectedGuide is null) return;
        AddDetailLine(selectedGuide.Name);
        if (GuideMode) detailsList.ContentNode.AddNode(new DetailSelectorRow<GuideLocation>(
            "Locations:", selectedGuide.Locations, guideLocation, l => l.Label,
            l => { guideLocation = l; guideLocationPending = guideDetailsPending = true; }, "Unknown") { Width = Math.Max(80, detailsList.Width - 24) });
        poleSelector = new DetailSelectorRow<FishingPole>(
            "Fishing Pole:", guidePoles, guidePole, p => p.Label,
            p => { guidePole = p; guideDetailsPending = true; },
            guideLocation?.Spearfishing == true ? "Not used (spearfishing)" : "No eligible poles") { Width = Math.Max(80, detailsList.Width - 24) };
        detailsList.ContentNode.AddNode(poleSelector);
        catchBody = new JournalListNode { Width = Math.Max(80, detailsList.Width - 24), FitContents = true };
        detailsList.ContentNode.AddNode(catchBody);
        RenderCatchBody();
    }

    private void RenderCatchBody()
    {
        if (catchBody is null || selectedGuide is null) return;
        catchBody.Clear();
        writingCatchBody = true;
        if (guideLocation is not null)
            foreach (string line in selectedGuide.GetDetails(guideLocation, guidePole)) AddDetailLine(line);
        else AddDetailLine("Location requirements unknown.");
        if (selectedGuide.Info.Count > 0) {
            AddDetailLine("");
            AddDetailLine("Description:");
            foreach (string line in selectedGuide.Info) AddDetailLine(line);
        }
        writingCatchBody = false;
        catchBody.RecalculateLayout();
        detailsList?.RecalculateSizes();
    }

    private void ReflowDetails()
    {
        if (detailsList is null) return;
        float width = Math.Max(80.0f, detailsList.Width - 24.0f);
        foreach (var label in detailsList.ContentNode.GetNodes<LabelTextNode>())
        {
            if (Math.Abs(label.Width - width) < 1) continue;
            label.Width = width;
            label.Height = Math.Max(24.0f, label.GetTextDrawSize(false).Y + 6.0f);
        }
        foreach (var row in detailsList.ContentNode.GetNodes<ItemDetailRow>()) row.Width = width;
        foreach (var row in detailsList.ContentNode.GetNodes<DetailSelectorRow<GuideLocation>>()) row.Width = width;
        foreach (var row in detailsList.ContentNode.GetNodes<DetailSelectorRow<FishingPole>>()) row.Width = width;
        if (catchBody is not null && Math.Abs(catchBody.Width - width) >= 1) {
            catchBody.Width = width;
            foreach (var label in catchBody.GetNodes<LabelTextNode>()) {
                label.Width = width;
                label.Height = Math.Max(24, label.GetTextDrawSize(false).Y + 6);
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
        detailsHint.IsVisible = configuration.EmbedFishDetails && selectedDetails is null;
    }

    private void AddDetailLine(string text)
    {
        if (detailsList is null) return;
        if (selectedGuide is not null && (text.Contains(":") && !selectedGuide.Info.Contains(text) || selectedGuide.ItemLinks.ContainsKey(text))) {
            (writingCatchBody ? catchBody! : detailsList.ContentNode).AddNode(new ItemDetailRow(text, selectedGuide.ItemLinks) { Width = Math.Max(80, detailsList.Width - 24) });
            return;
        }
        var label = new LabelTextNode
        {
            Width = Math.Max(80.0f, detailsList.Width - 24.0f),
            FontSize = 14,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            String = text,
        };
        label.Height = Math.Max(24.0f, label.GetTextDrawSize(false).Y + 6.0f);
        (writingCatchBody ? catchBody! : detailsList.ContentNode).AddNode(label);
    }
}
