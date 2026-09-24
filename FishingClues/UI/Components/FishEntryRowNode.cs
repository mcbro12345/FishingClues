using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

using FishingClues.Game.Models;

namespace FishingClues.UI.Components;

public sealed class FishEntryRowNode : ListButtonNode
{
    private const float CompactHeight = 42.0f;
    private const uint BadgeFontSize = 12;
    private const float BadgeLineHeight = 14.0f;
    // padding above/below the content, bigger with a badge so the glow encloses the text
    private const float PlainPadding = 2.0f;
    private const float BadgePadding = 5.0f;

    private const float IconSize = 38.0f;

    private readonly IconImageNode fishIcon;
    private readonly LabelTextNode unknownIcon;
    private LabelTextNode? favoriteButton;
    private LabelTextNode? availabilityBadge;
    private string availabilityRawText = "";
    private bool favoriteHovered;
    private Action clickAction;

    public JournalFish Fish { get; private set; }
    private Func<bool>? toggleFavorite;
    private bool favoriteSaved;

    public FishEntryRowNode(JournalFish fish, string label, Action onClick)
    {
        Width = 440.0f;
        Height = CompactHeight;
        String = label;
        clickAction = onClick;
        Fish = fish;
        OnClick = () => { if (!favoriteHovered) clickAction(); };

        LabelNode.TextFlags = TextFlags.None;
        // soft highlight edges grew with tall rows and faded the text, so pin them at 6px and stretch the middle
        HoverBackgroundNode.TopOffset = HoverBackgroundNode.BottomOffset = 6.0f;
        SelectedBackgroundNode.TopOffset = SelectedBackgroundNode.BottomOffset = 6.0f;
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

    public void Rebind(JournalFish fish, string label, Action onClick)
    {
        String = label;
        clickAction = onClick;
        Fish = fish;
        fishIcon.IconId = fish.IdentityVisible ? fish.IconId : 0;
        fishIcon.IsVisible = fish.IdentityVisible && fish.IconId != 0;
        unknownIcon.IsVisible = !fish.IdentityVisible;
    }

    public unsafe void SetFavoriteStar(bool favorite, Func<bool> toggle)
    {
        toggleFavorite = toggle;
        if (favoriteButton is not null)
        {
            UpdateStar(favorite);
            return;
        }
        Component->SoundEffectId = 1; // same click sound KamiToolKit's dropdown uses
        favoriteButton = new LabelTextNode {
            Size = new Vector2(28, 28), FontSize = 20,
            AlignmentType = AlignmentType.Center, TextFlags = TextFlags.None,
            ShowClickableCursor = true,
        };
        favoriteButton.AddEvent(AtkEventType.MouseDown, () => {
            if (!NativeMouseInput.IsLeftButtonPressed() || toggleFavorite is null) return;
            UpdateStar(toggleFavorite());
            Component->PlaySoundEffect();
        });
        favoriteButton.AddEvent(AtkEventType.MouseOver, () => { favoriteHovered = true; UpdateStar(favoriteSaved); });
        favoriteButton.AddEvent(AtkEventType.MouseOut, () => { favoriteHovered = false; UpdateStar(favoriteSaved); });
        favoriteButton.AttachNode(this, NodePosition.AfterAllSiblings);
        UpdateStar(favorite);
        OnSizeChanged();
    }

    private void UpdateStar(bool value)
    {
        favoriteSaved = value;
        if (favoriteButton is null) return;
        favoriteButton.String = value ? "★" : "☆";
        favoriteButton.TextColor = favoriteHovered ? Vector4.One : value ? new Vector4(1, 0.8f, 0.25f, 1) : new Vector4(0.7f, 0.7f, 0.7f, 1);
        favoriteButton.TextTooltip = value ? "Remove from favorites" : "Add to favorites";
    }

    public void ClearAvailability()
    {
        if (availabilityBadge is null) return;
        availabilityBadge.Dispose();
        availabilityBadge = null;
        availabilityRawText = "";
        wrappedBadgeSource = "";
        OnSizeChanged();
    }

    public void SetAvailability(bool available, string text, string tooltip)
    {
        if (availabilityBadge is null)
        {
            // default LineSpacing (24) is too tall for the small badge
            availabilityBadge = new LabelTextNode { FontSize = BadgeFontSize, LineSpacing = 14 };
            availabilityBadge.AttachNode(this, NodePosition.AfterAllSiblings);
        }
        availabilityRawText = text;
        availabilityBadge.TextColor = available ? new Vector4(0.45f, 0.95f, 0.45f, 1f) : new Vector4(0.95f, 0.4f, 0.4f, 1f);
        availabilityBadge.TextTooltip = tooltip;

        OnSizeChanged();
    }

    // greedy wrap in the badge font, keeps a time range on one line
    private string WrapBadgeText(string text, float width, out int lineCount)
    {
        const char Glue = '\u001F';
        string protectedText = TimePhrase.Replace(text, m => m.Value.Replace(' ', Glue));
        var lines = new List<string>();
        string line = "";
        foreach (string word in protectedText.Split(' '))
        {
            string token = word.Replace(Glue, ' ');
            string candidate = line.Length == 0 ? token : line + " " + token;
            if (line.Length > 0 && availabilityBadge!.GetTextDrawSize(candidate, false).X > width)
            {
                lines.Add(line);
                line = token;
            }
            else line = candidate;
        }
        if (line.Length > 0) lines.Add(line);
        lineCount = Math.Max(1, lines.Count);
        return string.Join("\n", lines);
    }

    // just the time range, so the lead-in word stays on the previous line
    private static readonly Regex TimePhrase = new(
        @"\d{1,2}:\d{2}(?:am|pm)?-\d{1,2}:\d{2}(?:am|pm)? ET(?: window)?\.?", RegexOptions.Compiled);

    private string wrappedBadgeSource = "";
    private int wrappedBadgeLines = 1;

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        float rightReserve = favoriteButton is null ? 56 : 90;

        // badge is always on its own line, broken by hand: native wrap can split a time range and its height lags a frame
        int badgeLines = 1;
        if (availabilityBadge is not null)
        {
            float wrappedWidth = Math.Max(80.0f, Width - 58.0f);
            availabilityBadge.AlignmentType = AlignmentType.Left;
            availabilityBadge.TextFlags = TextFlags.MultiLine;
            if (Math.Abs(availabilityBadge.Width - wrappedWidth) > 0.5f || wrappedBadgeSource != availabilityRawText)
            {
                availabilityBadge.Width = wrappedWidth;
                wrappedBadgeSource = availabilityRawText;
                availabilityBadge.String = WrapBadgeText(availabilityRawText, wrappedWidth, out wrappedBadgeLines);
            }
            badgeLines = wrappedBadgeLines;
        }

        float badgeHeight = availabilityBadge is null ? 0.0f : Math.Max(14.0f, badgeLines * BadgeLineHeight);
        if (availabilityBadge is not null) availabilityBadge.Height = badgeHeight;
        // the 28px name box is taller than the glyphs drawn in it
        float nameHeight = Math.Max(14.0f, LabelNode.GetTextDrawSize(false).Y);

        float stackHeight = nameHeight + badgeHeight;
        float contentHeight = Math.Max(IconSize, stackHeight);
        // Rows keep the compact height when everything fits and grow for extra badge lines.
        float padding = availabilityBadge is null ? PlainPadding : BadgePadding;
        float desiredHeight = Math.Max(CompactHeight, contentHeight + 2.0f * padding);
        float margin = Math.Max(0.0f, (desiredHeight - contentHeight) / 2.0f);

        float iconY = margin + (contentHeight - IconSize) / 2.0f;
        float nameY = margin + (contentHeight - stackHeight) / 2.0f;
        if (fishIcon is not null) fishIcon.Position = new Vector2(4.0f, iconY);
        if (unknownIcon is not null) unknownIcon.Position = new Vector2(4.0f, iconY);
        LabelNode.Position = new Vector2(50.0f, nameY);
        LabelNode.Size = new Vector2(Math.Max(20.0f, Width - rightReserve), nameHeight);
        // the star is centered on the fish icon
        if (favoriteButton is not null) favoriteButton.Position = new Vector2(Math.Max(50, Width - 30), iconY + (IconSize - favoriteButton.Height) / 2.0f);
        if (availabilityBadge is not null) availabilityBadge.Position = new Vector2(50.0f, nameY + nameHeight);

        if (Math.Abs(Height - desiredHeight) > 0.5f) Height = desiredHeight;
    }
}