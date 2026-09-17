using System;
using KamiToolKit.Nodes;

namespace FishingClues;

public sealed class JournalScrollingNode : ScrollingNode<JournalListNode>
{
    protected override void OnSizeChanged()
    {
        float previous = ScrollBarNode.ScrollPosition;
        base.OnSizeChanged();
        // Base updates the range before applying content width and laying out rows.
        // Refresh it after the final layout, then restore against the new range.
        ContentNode.RecalculateLayout();
        ScrollBarNode.UpdateScrollParams();
        ScrollBarNode.ScrollPosition = float.IsFinite(previous)
            ? Math.Clamp(previous, 0, Math.Max(0, ScrollBarNode.ScrollMaxPosition)) : 0;
    }
}
