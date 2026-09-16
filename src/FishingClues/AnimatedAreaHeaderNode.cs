using System;
using KamiToolKit.Nodes;

namespace FishingClues;

// Animate layout without a native Clip flag: these non-component layout nodes
// cannot safely establish an isolated clip scope in the game's draw list.
public sealed class AnimatedAreaHeaderNode : CollapsingHeaderNode
{
    private float progress;
    private float from;
    private float target;
    private long started;
    private bool ready;
    public void InitializeAnimation()
    {
        progress = target = IsCollapsed ? 0 : 1;
        ready = true;
        ClipListContents = false;
        RecalculateLayout();
    }

    public bool Tick()
    {
        if (!ready) return false;
        float next = IsCollapsed ? 0 : 1;
        if (next != target)
        {
            from = progress;
            target = next;
            started = Environment.TickCount64;
        }
        if (progress == target) return false;
        float t = Math.Clamp((Environment.TickCount64 - started) / 160.0f, 0, 1);
        progress = from + (target - from) * (t * t * (3 - 2 * t));
        RecalculateLayout();
        return true;
    }

    protected override void OnRecalculateLayout()
    {
        if (!ready) { base.OnRecalculateLayout(); return; }
        float fullHeight = 28 + FirstItemSpacing;
        foreach (var node in Nodes) fullHeight += node.Height + ItemSpacing;
        float visibleHeight = 28 + (fullHeight - 28) * progress;
        float y = 28 + FirstItemSpacing;
        foreach (var node in Nodes)
        {
            node.Y = y;
            // Reveal only complete rows; never clip text or affect sibling panes.
            node.IsVisible = progress > 0 && y + node.Height <= visibleHeight;
            if (FitWidth) node.Width = Width;
            y += node.Height + ItemSpacing;
        }
        Height = visibleHeight;
    }
}
