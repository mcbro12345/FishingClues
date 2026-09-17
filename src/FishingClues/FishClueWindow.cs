using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace FishingClues;

public sealed record FishClueSection(string Heading, IReadOnlyList<string> Lines);

public sealed class FishClueWindow(IReadOnlyList<FishClueSection> sections) : NativeAddon
{
    private ScrollingNode<VerticalListNode>? scrolling;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        scrolling = new ScrollingNode<VerticalListNode>
        {
            Position = ContentStartPosition,
            Size = ContentSize,
            AutoHideScrollBar = true,
            ScrollSpeed = 34,
            ContentNode =
            {
                FitWidth = true,
                FitContents = true,
                ItemSpacing = 2.0f,
            },
        };

        scrolling.ContentNode.AddNode(new LabelTextNode
        {
            Height = 28.0f,
            FontSize = 14,
            String = sections.Count == 1
                ? "Fishing details. Unknown fish names remain hidden."
                : $"{sections.Count} fish. Unknown names remain hidden.",
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
        });

        for (int i = 0; i < sections.Count; i++)
        {
            FishClueSection section = sections[i];
            if (i > 0)
            {
                scrolling.ContentNode.AddNode(new HorizontalLineNode
                {
                    Height = 2.0f,
                });
            }

            scrolling.ContentNode.AddNode(new CategoryTextNode
            {
                String = section.Heading,
            });

            foreach (string line in section.Lines)
            {
                bool longLine = line.StartsWith("Description:", StringComparison.Ordinal);
                scrolling.ContentNode.AddNode(new LabelTextNode
                {
                    Height = longLine ? 88.0f : 21.0f,
                    FontSize = 14,
                    String = line,
                    TextFlags = longLine ? TextFlags.WordWrap | TextFlags.MultiLine : TextFlags.Ellipsis,
                });
            }
        }

        scrolling.RecalculateSizes();
        scrolling.AttachNode(this);
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        scrolling = null;
    }
}
