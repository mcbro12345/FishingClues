using System;
using KamiToolKit.Nodes;

namespace FishingClues;

public sealed class AnimatedAreaHeaderNode : CollapsingHeaderNode
{
    private float progress;
    private float from;
    private float target;
    private long started;
    private bool ready;
    public bool RestoreExpandedOnNextTick { get; set; }
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
        if (RestoreExpandedOnNextTick) {
            RestoreExpandedOnNextTick = false;
            IsCollapsed = false;
            RecalculateLayout();
            return true;
        }
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
        float layoutProgress = Math.Min(1, progress * 2);
        float opacity = Math.Max(0, progress * 2 - 1);
        float visibleHeight = 28 + (fullHeight - 28) * layoutProgress;
        float y = 28 + FirstItemSpacing;
        foreach (var node in Nodes)
        {
            node.Y = y;
            node.IsVisible = opacity > 0;
            node.Alpha = opacity;
            if (FitWidth) node.Width = Width;
            y += node.Height + ItemSpacing;
        }
        Height = visibleHeight;
    }
}
