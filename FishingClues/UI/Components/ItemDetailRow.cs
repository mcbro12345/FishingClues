using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KamiToolKit.Nodes;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FishingClues.Game.Logic;

namespace FishingClues.UI.Components;

// one details line. "Label: contents" gets a title row with the contents indented under it, otherwise just contents. SetContent reuses the nodes
public sealed class ItemDetailRow : ResNode
{
    private const float LineHeight = 18.0f;
    private const float TitleHeight = 18.0f;
    private const float SectionGap = 5.0f;
    private const float ContentIndent = 16.0f;
    private const uint TitleFontSize = 14;

    private static readonly Vector4 TitleColor = new(0.93f, 0.93f, 0.93f, 1.0f);
    private static readonly Vector4 LinkColor = new(0.55f, 0.82f, 1.0f, 1.0f);
    private static readonly Vector4 LinkHoverColor = Vector4.One;

    private readonly List<LabelTextNode> segments = new();
    private readonly Dictionary<LabelTextNode, uint> segmentItems = new();
    private LabelTextNode? title;
    private bool layingOut;
    private float lineHeight = LineHeight;

    public ItemDetailRow(string text, IReadOnlyDictionary<string, uint> links, uint fontSize = 14, FontType? fontType = null)
    {
        Width = 450;
        SetContent(text, links, fontSize, fontType);
    }

    public void SetContent(string text, IReadOnlyDictionary<string, uint> links, uint fontSize = 14, FontType? fontType = null)
    {
        lineHeight = LineHeight + (fontSize - 14) * 1.5f;
        int colon = text.IndexOf(':');
        if (colon > 0)
        {
            SetTitle(text[..colon]);
            text = text[(colon + 1)..].TrimStart();
        }
        else if (title is not null)
        {
            title.Dispose();
            title = null;
        }

        var pieces = SplitIntoSegments(text, links);
        for (int i = 0; i < pieces.Count; i++)
        {
            LabelTextNode node = i < segments.Count ? segments[i] : CreateSegment();
            ConfigureSegment(node, pieces[i].Text, pieces[i].ItemId, fontSize, fontType);
        }
        for (int i = segments.Count - 1; i >= pieces.Count; i--)
        {
            segmentItems.Remove(segments[i]);
            segments[i].Dispose();
            segments.RemoveAt(i);
        }
        Layout();
    }

    private void SetTitle(string text)
    {
        if (title is null)
        {
            title = new LabelTextNode { FontSize = TitleFontSize, Height = TitleHeight, Width = 1000, TextColor = TitleColor };
            title.AttachNode(this);
        }
        title.String = text;
        title.Width = Math.Max(1, title.GetTextDrawSize(false).X + 2);
    }

    private static List<(string Text, uint ItemId)> SplitIntoSegments(string text, IReadOnlyDictionary<string, uint> links)
    {
        var pieces = new List<(string, uint)>();
        while (text.Length > 0) {
            var match = links.Where(p => text.StartsWith(p.Key, StringComparison.Ordinal))
                .OrderByDescending(p => p.Key.Length).FirstOrDefault();
            int length = match.Key?.Length ?? 1;
            // an item name and its trailing comma wrap together, never split onto separate lines
            if (match.Key is not null && length < text.Length && text[length] == ',') {
                length++;
                if (length < text.Length && text[length] == ' ') length++;
            }
            if (match.Key is null) {
                while (length < text.Length && !char.IsWhiteSpace(text[length - 1]) && !links.Keys.Any(k => text.AsSpan(length).StartsWith(k, StringComparison.Ordinal))) length++;
            }
            pieces.Add((text[..length], match.Key is not null ? match.Value : 0u));
            text = text[length..];
        }
        return pieces;
    }

    private LabelTextNode CreateSegment()
    {
        var node = new LabelTextNode { Width = 1000 };
        Vector4 plainColor = node.TextColor;
        node.AddEvent(AtkEventType.MouseOver, () => { if (IsLink(node)) node.TextColor = LinkHoverColor; });
        node.AddEvent(AtkEventType.MouseOut, () => node.TextColor = IsLink(node) ? LinkColor : plainColor);
        node.AddEvent(AtkEventType.MouseDown, () => {
            if (segmentItems.TryGetValue(node, out uint itemId) && itemId != 0 && NativeMouseInput.IsRightButtonHeld())
                ItemContextMenuService.RequestItemMenu(itemId);
        });
        node.AttachNode(this);
        segments.Add(node);
        segmentPlainColors[node] = plainColor;
        return node;
    }

    private readonly Dictionary<LabelTextNode, Vector4> segmentPlainColors = new();

    private bool IsLink(LabelTextNode node) => segmentItems.TryGetValue(node, out uint itemId) && itemId != 0;

    private void ConfigureSegment(LabelTextNode node, string text, uint itemId, uint fontSize, FontType? fontType)
    {
        segmentItems[node] = itemId;
        node.String = text;
        node.FontSize = fontSize;
        node.FontType = fontType ?? FontType.Axis;
        node.Height = lineHeight;
        // words are placed one after another with their trailing space, extra padding made the gaps too wide
        node.Width = 1000;
        node.Width = Math.Max(1, node.GetTextDrawSize(false).X - 1);
        node.ItemTooltip = itemId;
        node.TextColor = itemId != 0 ? LinkColor : segmentPlainColors[node];
        node.ShowClickableCursor = itemId != 0;
    }

    protected override void OnSizeChanged() { base.OnSizeChanged(); Layout(); }

    private void Layout()
    {
        if (segments is null || layingOut) return;
        layingOut = true;
        float y = 0;
        float left = 0;
        if (title is not null) {
            y = SectionGap;
            title.Position = new Vector2(0, y);
            y += TitleHeight;
            left = ContentIndent;
        }
        float x = left;
        foreach (var node in segments) {
            if (x > left && x + node.Width > Width) { x = left; y += lineHeight; }
            node.Position = new Vector2(x, y);
            x += node.Width;
        }
        // a title with nothing under it has no content row to add
        Height = segments.Count == 0 ? y : y + lineHeight;
        layingOut = false;
    }
}
