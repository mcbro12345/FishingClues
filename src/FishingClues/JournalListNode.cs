using System;
using KamiToolKit.Nodes;

namespace FishingClues;

// offscreen rows still get laid out - skipping them would overlap and compress the rest
public sealed class JournalListNode : VerticalListNode
{
    protected override void OnRecalculateLayout()
    {
        float y = FirstItemSpacing;
        foreach (var node in Nodes)
        {
            node.Y = y;
            if (FitWidth) node.Width = Width;
            y += node.Height + ItemSpacing;
        }
        if (FitContents)
            Height = Math.Max(0, y - (Nodes.Count > 0 ? ItemSpacing : 0));
    }
}
