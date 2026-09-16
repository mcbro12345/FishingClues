using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KamiToolKit.Nodes;

namespace FishingClues;

// A native popup selector, not a collapsing section. The toolkit closes the
// popup after selection; its callback only queues next-frame content updates.
public sealed class DetailSelectorRow<T> : ResNode
{
    private readonly SizedDropDownNode<T> selector;
    private readonly LabelTextNode selectedLabel;
    private readonly Func<T, string> labelText;
    private readonly string emptyText;
    private string fullSelectedText = "";
    private void UpdateSelectedLabel(T? selected)
    {
        fullSelectedText = selected is null ? emptyText : labelText(selected);
        if (selectedLabel is not null) selectedLabel.String = fullSelectedText;
    }
    public void SetOptions(IReadOnlyList<T> options, T? selected)
    {
        selector.Options = options.ToList();
        selector.SelectedOption = selected;
        selector.IsEnabled = options.Count > 0;
        selector.ItemTooltip = selected is FishingPole pole ? pole.ItemId : 0;
        UpdateSelectedLabel(selected);
        selector.ResizePopup();
    }

    public DetailSelectorRow(string title, IReadOnlyList<T> options, T? selected,
        Func<T, string> text, Action<T> changed, string empty)
    {
        labelText = text;
        emptyText = empty;
        selector = new SizedDropDownNode<T> {
            GetLabelFunction = entry => text(entry), MaxListOptions = 7, Options = options.ToList(),
            PlaceholderString = options.Count == 0 ? empty : null,
            SelectedOption = selected, OnOptionSelected = entry => {
                selector!.ItemTooltip = entry is FishingPole rod ? rod.ItemId : 0;
                UpdateSelectedLabel(entry);
                changed(entry);
            },
            IsEnabled = options.Count > 0,
        };
        if (selected is FishingPole pole) selector.ItemTooltip = pole.ItemId;
        selector.AttachNode(this);
        selector.LabelNode.IsVisible = false;
        selectedLabel = new LabelTextNode {
            Position = new Vector2(20, 0), Height = 25, Width = 418, FontSize = 12,
            TextFlags = FFXIVClientStructs.FFXIV.Component.GUI.TextFlags.Ellipsis,
        };
        selectedLabel.AttachNode(this);
        UpdateSelectedLabel(selected);
        Size = new Vector2(450, 28);
        selector.ResizePopup();
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        if (selector is null) return;
        selector.Position = Vector2.Zero;
        selector.Size = new Vector2(Math.Max(80, Width), 28);
        selector.LabelNode.IsVisible = false;
        if (selectedLabel is not null) {
            selectedLabel.Width = Math.Max(1, Width - 32);
            selectedLabel.String = fullSelectedText;
        }
    }
}
