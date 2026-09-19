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
    private JournalListNode? catchBody;
    // Minimum height of a plain (unlabelled) line of details text.
    private const float TightLineHeight = 18.0f;
    private bool writingCatchBody;

    private void ClearDetails()
    {
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HideTooltip();
        partialHover = null;
        catchBody = null;
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
        detailsList.ContentNode.Clear();
        if (selectedGuide is not null) {
            RenderGuideDetails();
        }
        else if (selectedDetails is { } section)
        {
            // no headroom needed above a plain heading (only the big guide name has it)
            detailsList.ContentNode.FirstItemSpacing = 0.0f;
            string heading = section.Heading == "????" && unknownFishNumbers.TryGetValue(selectedFish, out int number)
                ? $"Unknown Fish #{number}"
                : section.Heading;
            AddDetailLine(heading);
            foreach (string line in section.Lines) AddDetailLine(line);
        }
        detailsList.RecalculateSizes();
        UpdateDetailsHint();
    }

    private void RenderGuideDetails()
    {
        if (detailsList is null || selectedGuide is null) return;
        // an undiscovered fish is headed "Unknown Fish #N", numbered like its list entry
        string name = !GuideMode && unknownFishNumbers.TryGetValue(selectedFish, out int unknownNumber)
            ? $"Unknown Fish {unknownNumber}" // this font draws '#' as a numero sign, so no symbol
            : selectedGuide.Name;
        // the big name's own box already carries headroom (see AddDetailLine), so the
        // list adds none above it
        detailsList.ContentNode.FirstItemSpacing = 0.0f;
        // the fish's name, in the same font and size as the Region / Area / Fish headings
        AddDetailLine(name, 16, FontType.Jupiter);
        if (GuideMode) detailsList.ContentNode.AddNode(new DetailSelectorRow<GuideLocation>(
            "Locations:", selectedGuide.Locations, guideLocation, l => l.Label,
            l => { guideLocation = l; guideLocationPending = guideDetailsPending = true; }, "Unknown") { Width = Math.Max(80, detailsList.Width - 24) });
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
            // (no blank line first: a section title already has space above it)
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
            label.Height = Math.Max(TightLineHeight, label.GetTextDrawSize(false).Y + 2.0f) + (label.FontSize > 14 ? 3.0f : 0.0f);
        }
        foreach (var row in detailsList.ContentNode.GetNodes<ItemDetailRow>()) row.Width = width;
        foreach (var row in detailsList.ContentNode.GetNodes<DetailSelectorRow<GuideLocation>>()) row.Width = width;
        if (catchBody is not null && Math.Abs(catchBody.Width - width) >= 1) {
            catchBody.Width = width;
            foreach (var label in catchBody.GetNodes<LabelTextNode>()) {
                label.Width = width;
                label.Height = Math.Max(TightLineHeight, label.GetTextDrawSize(false).Y + 2);
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

    private void AddDetailLine(string text, uint fontSize = 14, FontType? fontType = null)
    {
        if (detailsList is null) return;
        if (selectedGuide is not null && (text.Contains(":") && !selectedGuide.Info.Contains(text) || selectedGuide.ItemLinks.ContainsKey(text))) {
            (writingCatchBody ? catchBody! : detailsList.ContentNode).AddNode(new ItemDetailRow(text, selectedGuide.ItemLinks, fontSize, fontType) { Width = Math.Max(80, detailsList.Width - 24) });
            return;
        }
        var label = new LabelTextNode
        {
            Width = Math.Max(80.0f, detailsList.Width - 24.0f),
            FontSize = fontSize,
            // wrapped lines sit 18px apart (the labels' default of 24 is airier than we want)
            LineSpacing = 18 + (fontSize - 14) * 3 / 2,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            String = text,
        };
        if (fontType is FontType font) label.FontType = font;
        label.Height = Math.Max(TightLineHeight, label.GetTextDrawSize(false).Y + 2.0f);
        if (fontSize > 14)
        {
            // the big heading's glyphs rise above a snug box and get clipped at the
            // list's top edge: give it headroom and sit the text at the bottom of it
            label.Height += 3.0f;
            label.AlignmentType = AlignmentType.BottomLeft;
        }
        (writingCatchBody ? catchBody! : detailsList.ContentNode).AddNode(label);
    }
}
