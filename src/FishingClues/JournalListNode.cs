using System;
using KamiToolKit.Nodes;

namespace FishingClues;

// Native scroll culling can change visibility flags. Offscreen rows still own
// their layout space; excluding them compresses the range and overlaps rows.
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
