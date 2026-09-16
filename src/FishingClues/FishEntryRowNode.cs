using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace FishingClues;

public sealed class FishEntryRowNode : ListButtonNode
{
    private readonly IconImageNode fishIcon;
    private readonly LabelTextNode unknownIcon;

    public FishEntryRowNode(JournalFish fish, string label, Action onClick)
    {
        Width = 440.0f;
        Height = 44.0f;
        String = label;
        TextTooltip = fish.IsCaught ? fish.Name : "Unknown fish";
        OnClick = onClick;

        LabelNode.Position = new Vector2(48.0f, 10.0f);
        LabelNode.TextFlags = TextFlags.None;
        fishIcon = new IconImageNode
        {
            Position = new Vector2(7.0f, 6.0f),
            Size = new Vector2(32.0f, 32.0f),
            FitTexture = true,
            IconId = fish.IsCaught ? fish.IconId : 0,
            IsVisible = fish.IsCaught && fish.IconId != 0,
        };
        fishIcon.AttachNode(this, NodePosition.AfterAllSiblings);

        unknownIcon = new LabelTextNode
        {
            Position = new Vector2(9.0f, 7.0f),
            Size = new Vector2(28.0f, 28.0f),
            FontSize = 22,
            AlignmentType = AlignmentType.Center,
            String = "?",
            IsVisible = !fish.IsCaught,
        };
        unknownIcon.AttachNode(this, NodePosition.AfterAllSiblings);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        LabelNode.Position = new Vector2(48.0f, 10.0f);
        LabelNode.Size = new Vector2(Math.Max(20.0f, Width - 54.0f), 28.0f);
    }
}
