using System;
using KamiToolKit.Nodes;

namespace FishingClues;

public sealed class JournalScrollingNode : ScrollingNode<JournalListNode>
{
    protected override void OnSizeChanged()
    {
        float previous = ScrollBarNode.ScrollPosition;
        base.OnSizeChanged();
        // base sets the scroll range before rows are laid out, so redo it after
        ContentNode.RecalculateLayout();
        ScrollBarNode.UpdateScrollParams();
        ScrollBarNode.ScrollPosition = float.IsFinite(previous)
            ? Math.Clamp(previous, 0, Math.Max(0, ScrollBarNode.ScrollMaxPosition)) : 0;
    }
}
