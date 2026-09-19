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
    private const uint BadgeFontSize = 12;
    // The badge's LineSpacing (see SetAvailability): one text line's height.
    private const float BadgeLineHeight = 14.0f;
    private const float VerticalPadding = 6.0f;

    private const float IconSize = 38.0f;

    private readonly IconImageNode fishIcon;
    private readonly LabelTextNode unknownIcon;
    private LabelTextNode? favoriteButton;
    private LabelTextNode? availabilityBadge;
    private string availabilityRawText = "";
    private bool favoriteHovered;

    public FishEntryRowNode(JournalFish fish, string label, Action onClick)
    {
        Width = 440.0f;
        Height = CompactHeight;
        String = label;
        OnClick = () => { if (!favoriteHovered) onClick(); };

        LabelNode.TextFlags = TextFlags.None;
        // The hover and selected highlights are soft at their top and bottom edges. Stretched
        // over a tall (multi-line) row those soft bands grew with it and left the text in
        // the faded part, so the bands are pinned at 6px and only the solid middle stretches.
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
        if (availabilityBadge is null)
        {
            // default LineSpacing (24) is tuned for 14pt text, too tall for our small badge
            availabilityBadge = new LabelTextNode { FontSize = BadgeFontSize, LineSpacing = 14 };
            availabilityBadge.AttachNode(this, NodePosition.AfterAllSiblings);
        }
        availabilityRawText = text;
        availabilityBadge.TextColor = available ? new Vector4(0.45f, 0.95f, 0.45f, 1f) : new Vector4(0.95f, 0.4f, 0.4f, 1f);
        availabilityBadge.TextTooltip = tooltip;

        OnSizeChanged();
    }

    // Greedy word wrap that measures each candidate line with the badge's own
    // font, except that a time range stays whole on one line: "during
    // 7:00pm-9:00pm ET." / "the 9:00pm-3:00am ET window." move to the next
    // line together instead of being cut in the middle.
    private string WrapBadgeText(string text, float width, out int lineCount)
    {
        const char Glue = '';
        string protectedText = TimePhrase.Replace(text, m => m.Value.Replace(' ', Glue));
        var lines = new System.Collections.Generic.List<string>();
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

    // "7:00pm-9:00pm ET." / "9:00pm-3:00am ET window." / "21:00-03:00 ET" - just
    // the range itself, so a lead-in word like "during" stays on the line before.
    private static readonly System.Text.RegularExpressions.Regex TimePhrase = new(
        @"\d{1,2}:\d{2}(?:am|pm)?-\d{1,2}:\d{2}(?:am|pm)? ET(?: window)?\.?",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private string wrappedBadgeSource = "";
    private string wrappedBadgeText = "";
    private int wrappedBadgeLines = 1;

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        float favoriteReserve = favoriteButton is null ? 8.0f : 38.0f;
        float rightReserve = favoriteButton is null ? 56 : 90;

        // The availability badge always sits on its own line under the fish
        // name, never squeezed onto the name's own line to one side of it -
        // an earlier version tried to fit short badge text inline next to
        // the name when there was room, but that made the badge jump
        // between two different spots depending on how long the current
        // text happened to be (for example, switching Settings' 12-hour
        // time format changes the badge string's length, which could flip
        // it between the inline and stacked spot even though nothing about
        // the row itself changed) - always stacking it below the name is
        // consistent regardless of the text or which time format is active.
        int badgeLines = 1;
        if (availabilityBadge is not null)
        {
            float wrappedWidth = Math.Max(80.0f, Width - 58.0f);
            availabilityBadge.AlignmentType = AlignmentType.Left;
            // Line breaks are inserted by hand (WrapBadgeText) rather than left to
            // the native word wrap: it can split a time range across lines
            // ("7:00pm-" / "9:00pm ET."), and the wrapped height it reports only
            // catches up a frame after the text is set, which left the row (and
            // its hover glow) one line too short.
            availabilityBadge.TextFlags = TextFlags.MultiLine;
            if (Math.Abs(availabilityBadge.Width - wrappedWidth) > 0.5f || wrappedBadgeSource != availabilityRawText)
            {
                availabilityBadge.Width = wrappedWidth;
                wrappedBadgeSource = availabilityRawText;
                wrappedBadgeText = WrapBadgeText(availabilityRawText, wrappedWidth, out wrappedBadgeLines);
                availabilityBadge.String = wrappedBadgeText;
            }
            badgeLines = wrappedBadgeLines;
        }

        float textHeight = availabilityBadge is null ? 0.0f : Math.Max(14.0f, badgeLines * BadgeLineHeight);
        if (availabilityBadge is not null) availabilityBadge.Height = textHeight;
        // the 28px name box is taller than the glyphs actually drawn in it
        float nameHeight = Math.Max(14.0f, LabelNode.GetTextDrawSize(false).Y);

        const float lineGap = 0.0f;
        float stackHeight = availabilityBadge is null ? nameHeight : nameHeight + lineGap + textHeight;
        float contentHeight = Math.Max(IconSize, stackHeight);
        // rows match the compact height when everything fits; if the availability
        // text needs a second (or third) line, grow the row instead of clipping it
        // ...with breathing room above and below the text, so the row (and its
        // hover glow) visibly encloses every line instead of ending flush with them.
        float desiredHeight = Math.Max(CompactHeight, contentHeight + 2.0f * VerticalPadding);
        float margin = Math.Max(0.0f, (desiredHeight - contentHeight) / 2.0f);

        float iconY = margin + (contentHeight - IconSize) / 2.0f;
        float nameY = margin + (contentHeight - stackHeight) / 2.0f;
        if (fishIcon is not null) fishIcon.Position = new Vector2(4.0f, iconY);
        if (unknownIcon is not null) unknownIcon.Position = new Vector2(4.0f, iconY);
        LabelNode.Position = new Vector2(50.0f, nameY);
        LabelNode.Size = new Vector2(Math.Max(20.0f, Width - rightReserve), nameHeight);
        // The star (28px tall) is centered on the fish icon's vertical middle.
        if (favoriteButton is not null) favoriteButton.Position = new Vector2(Math.Max(50, Width - 30), iconY + (IconSize - favoriteButton.Height) / 2.0f);
        if (availabilityBadge is not null) availabilityBadge.Position = new Vector2(50.0f, nameY + nameHeight + lineGap);

        if (Math.Abs(Height - desiredHeight) > 0.5f) Height = desiredHeight;
    }
}
