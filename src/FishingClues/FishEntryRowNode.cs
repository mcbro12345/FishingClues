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
    private LabelTextNode? favoriteButton;
    private bool favoriteHovered;

    public FishEntryRowNode(JournalFish fish, string label, Action onClick)
    {
        Width = 440.0f;
        Height = 44.0f;
        String = label;
        TextTooltip = fish.IdentityVisible ? fish.Name : "Unknown fish";
        OnClick = () => { if (!favoriteHovered) onClick(); };

        LabelNode.Position = new Vector2(48.0f, 10.0f);
        LabelNode.TextFlags = TextFlags.None;
        fishIcon = new IconImageNode
        {
            Position = new Vector2(7.0f, 6.0f),
            Size = new Vector2(32.0f, 32.0f),
            FitTexture = true,
            IconId = fish.IdentityVisible ? fish.IconId : 0,
            IsVisible = fish.IdentityVisible && fish.IconId != 0,
        };
        fishIcon.AttachNode(this, NodePosition.AfterAllSiblings);

        unknownIcon = new LabelTextNode
        {
            Position = new Vector2(9.0f, 7.0f),
            Size = new Vector2(28.0f, 28.0f),
            FontSize = 22,
            AlignmentType = AlignmentType.Center,
            String = "?",
            IsVisible = !fish.IdentityVisible,
        };
        unknownIcon.AttachNode(this, NodePosition.AfterAllSiblings);
    }

    public unsafe void AddFavoriteStar(bool favorite, Func<bool> toggle)
    {
        favoriteButton = new LabelTextNode {
            Size = new Vector2(28, 28), FontSize = 20,
            AlignmentType = AlignmentType.Center, TextFlags = TextFlags.None,
            ShowClickableCursor = true,
        };
        bool saved = favorite;
        void Update(bool value) {
            saved = value;
            favoriteButton.String = value ? "★" : "☆";
            favoriteButton.TextColor = favoriteHovered ? Vector4.One : value ? new Vector4(1, 0.8f, 0.25f, 1) : new Vector4(0.7f, 0.7f, 0.7f, 1);
            favoriteButton.TextTooltip = value ? "Remove from favorites" : "Add to favorites";
        }
        favoriteButton.AddEvent(AtkEventType.MouseDown, () => {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (framework != null && (framework->CursorInputs.MouseButtonPressedFlags & FFXIVClientStructs.FFXIV.Client.System.Input.MouseButtonFlags.LBUTTON) != 0)
                Update(toggle());
        });
        favoriteButton.AddEvent(AtkEventType.MouseOver, () => { favoriteHovered = true; Update(saved); });
        favoriteButton.AddEvent(AtkEventType.MouseOut, () => { favoriteHovered = false; Update(saved); });
        favoriteButton.AttachNode(this, NodePosition.AfterAllSiblings);
        Update(favorite);
        OnSizeChanged();
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        LabelNode.Position = new Vector2(48.0f, 10.0f);
        LabelNode.Size = new Vector2(Math.Max(20.0f, Width - (favoriteButton is null ? 54 : 88)), 28.0f);
        if (favoriteButton is not null) favoriteButton.Position = new Vector2(Math.Max(48, Width - 30), 8);
    }
}
