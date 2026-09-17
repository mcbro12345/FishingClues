using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

using FishingClues.Game.Models;

namespace FishingClues.UI.Components;

public sealed class FishEntryRowNode : ListButtonNode
{
    private const float CompactHeight = 44.0f;
    private const int WrappedBadgeTextLengthThreshold = 16;
    private const uint BadgeFontSize = 12;

    private const float IconSize = 38.0f;

    private readonly IconImageNode fishIcon;
    private readonly LabelTextNode unknownIcon;
    private LabelTextNode? favoriteButton;
    private LabelTextNode? availabilityBadge;
    private bool availabilityWrapped;
    private bool favoriteHovered;

    public FishEntryRowNode(JournalFish fish, string label, Action onClick)
    {
        Width = 440.0f;
        Height = CompactHeight;
        String = label;
        TextTooltip = fish.IdentityVisible ? fish.Name : "Unknown fish";
        OnClick = () => { if (!favoriteHovered) onClick(); };

        LabelNode.TextFlags = TextFlags.None;
        fishIcon = new IconImageNode
        {
            Size = new Vector2(IconSize, IconSize),
            FitTexture = true,
            IconId = fish.IdentityVisible ? fish.IconId : 0,
            IsVisible = fish.IdentityVisible && fish.IconId != 0,
        };
        fishIcon.AttachNode(this, NodePosition.AfterAllSiblings);

        unknownIcon = new LabelTextNode
        {
            Size = new Vector2(IconSize, IconSize),
            FontSize = 26,
            AlignmentType = AlignmentType.Center,
            String = "?",
            IsVisible = !fish.IdentityVisible,
        };
        unknownIcon.AttachNode(this, NodePosition.AfterAllSiblings);
    }

    public unsafe void AddFavoriteStar(bool favorite, Func<bool> toggle)
    {
        Component->SoundEffectId = 1; // same click sound KamiToolKit's dropdown uses
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
            if (!NativeMouseInput.IsLeftButtonPressed()) return;
            Update(toggle());
            Component->PlaySoundEffect();
        });
        favoriteButton.AddEvent(AtkEventType.MouseOver, () => { favoriteHovered = true; Update(saved); });
        favoriteButton.AddEvent(AtkEventType.MouseOut, () => { favoriteHovered = false; Update(saved); });
        favoriteButton.AttachNode(this, NodePosition.AfterAllSiblings);
        Update(favorite);
        OnSizeChanged();
    }

    public void SetAvailability(bool available, string text, string tooltip)
    {
        bool wrapped = text.Length > WrappedBadgeTextLengthThreshold;
        if (availabilityBadge is null)
        {
            // default LineSpacing (24) is tuned for 14pt text, too tall for our small badge
            availabilityBadge = new LabelTextNode { FontSize = BadgeFontSize, LineSpacing = 14 };
            availabilityBadge.AttachNode(this, NodePosition.AfterAllSiblings);
        }
        availabilityWrapped = wrapped;
        availabilityBadge.AlignmentType = wrapped ? AlignmentType.Left : AlignmentType.Right;
        availabilityBadge.TextFlags = wrapped ? TextFlags.WordWrap | TextFlags.MultiLine : TextFlags.Ellipsis;
        availabilityBadge.TextColor = available ? new Vector4(0.45f, 0.95f, 0.45f, 1f) : new Vector4(0.95f, 0.4f, 0.4f, 1f);
        availabilityBadge.TextTooltip = tooltip;
        availabilityBadge.String = text;

        OnSizeChanged();
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        bool wrapped = availabilityBadge is not null && availabilityWrapped;
        float rightReserve = favoriteButton is null ? 56 : 90;
        if (availabilityBadge is not null && !wrapped) rightReserve += 82;

        if (!wrapped)
        {
            float iconY = (CompactHeight - IconSize) / 2.0f;
            float labelY = (CompactHeight - 28.0f) / 2.0f;
            if (fishIcon is not null) fishIcon.Position = new Vector2(4.0f, iconY);
            if (unknownIcon is not null) unknownIcon.Position = new Vector2(4.0f, iconY);
            LabelNode.Position = new Vector2(50.0f, labelY);
            LabelNode.Size = new Vector2(Math.Max(20.0f, Width - rightReserve), 28.0f);
            if (favoriteButton is not null) favoriteButton.Position = new Vector2(Math.Max(50, Width - 30), labelY);

            if (availabilityBadge is not null)
            {
                availabilityBadge.Size = new Vector2(78.0f, 20.0f);
                float badgeRight = Width - (favoriteButton is null ? 8.0f : 38.0f);
                availabilityBadge.Position = new Vector2(Math.Max(48.0f, badgeRight - 78.0f), 12.0f);
            }
            if (Math.Abs(Height - CompactHeight) > 0.5f) Height = CompactHeight;
            return;
        }

        float wrappedWidth = Math.Max(80.0f, Width - 58.0f);
        if (Math.Abs(availabilityBadge!.Width - wrappedWidth) > 0.5f) availabilityBadge.Width = wrappedWidth;
        float textHeight = Math.Max(14.0f, availabilityBadge.GetTextDrawSize(false).Y);
        availabilityBadge.Height = textHeight;
        // the 28px name box is taller than the glyphs actually drawn in it
        float nameHeight = Math.Max(14.0f, LabelNode.GetTextDrawSize(false).Y);

        const float lineGap = 0.0f;
        float stackHeight = nameHeight + lineGap + textHeight;
        float contentHeight = Math.Max(IconSize, stackHeight);
        // wrapped rows match the compact height when they fit; if the availability
        // text needs a second (or third) line, grow the row instead of clipping it
        float desiredHeight = Math.Max(CompactHeight, contentHeight);
        float margin = Math.Max(0.0f, (desiredHeight - contentHeight) / 2.0f);

        float wrappedIconY = margin + (contentHeight - IconSize) / 2.0f;
        float nameY = margin + (contentHeight - stackHeight) / 2.0f;
        if (fishIcon is not null) fishIcon.Position = new Vector2(4.0f, wrappedIconY);
        if (unknownIcon is not null) unknownIcon.Position = new Vector2(4.0f, wrappedIconY);
        LabelNode.Position = new Vector2(50.0f, nameY);
        LabelNode.Size = new Vector2(Math.Max(20.0f, Width - rightReserve), nameHeight);
        if (favoriteButton is not null) favoriteButton.Position = new Vector2(Math.Max(50, Width - 30), nameY);
        availabilityBadge.Position = new Vector2(50.0f, nameY + nameHeight + lineGap);

        if (Math.Abs(Height - desiredHeight) > 0.5f) Height = desiredHeight;
    }
}
