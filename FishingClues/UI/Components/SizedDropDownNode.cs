using System;
using System.Reflection;
using KamiToolKit.Nodes;
using KamiToolKit.BaseTypes;

namespace FishingClues.UI.Components;
// adapter for the bundled toolkit version, popup nodes have no public sizing API
public sealed class SizedDropDownNode<T> : DropDownNode<T>
{
    public SizedDropDownNode() { OnUncollapsed += ResizePopup; }
    protected override void OnSizeChanged() {
        base.OnSizeChanged();
        if (LabelNode == null) return;
        LabelNode.Width = Math.Max(1, Width - 32);
        // SetText ellipsizes right away, restore the text AFTER resizing or it stays shortened
        if (SelectedOption is not null && GetLabelFunction is not null) LabelNode.String = GetLabelFunction(SelectedOption);
        ResizePopup();
    }

    private N? Part<N>(string name) where N : NodeBase => typeof(DropDownNode<T>).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this) as N;
    public void ResizePopup()
    {
        var popup = Part<KamiToolKit.Nodes.Simplified.SimpleComponentNode>("PopupContainerNode");
        var background = Part<KamiToolKit.Nodes.Simplified.SimpleNineGridNode>("PopupBackgroundNode");
        var list = Part<VerticalListNode>("PopupButtonListNode");
        var scroll = Part<ScrollBarNode>("PopupScrollbarNode");
        if (popup == null || background == null || list == null || scroll == null) return;
        float longest = Width;
        using (var measure = new LabelTextNode { FontSize = 14, Width = 4096, Height = 24 })
            foreach (var option in Options) { measure.String = GetLabelFunction?.Invoke(option) ?? ""; longest = Math.Max(longest, measure.GetTextDrawSize(false).X + 48); }
        popup.Width = longest; background.Width = longest; list.Width = longest - 24; scroll.X = longest - 14;
        list.RecalculateLayout();
        int index = Math.Max(0, (int)(scroll.ScrollPosition / 22));
        foreach (var button in list.GetNodes<ListButtonNode>()) {
            button.Width = longest - 24;
            button.LabelNode.Width = longest - 40;
            if (index < Options.Count && GetLabelFunction is not null) button.String = GetLabelFunction(Options[index++]);
        }
        LabelNode.Width = Math.Max(1, Width - 32);
        if (SelectedOption is not null && GetLabelFunction is not null) LabelNode.String = GetLabelFunction(SelectedOption);
    }
}
