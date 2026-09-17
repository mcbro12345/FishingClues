using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KamiToolKit.Nodes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FishingClues;

public sealed class ItemDetailRow : ResNode
{
    private readonly List<LabelTextNode> segments = new();
    private bool layingOut;
    public unsafe ItemDetailRow(string text, IReadOnlyDictionary<string, uint> links)
    {
        int prefixLength = text.IndexOf(':') + 1;
        bool first = true;
        while (text.Length > 0) {
            var match = links.Where(p => text.StartsWith(p.Key, StringComparison.Ordinal))
                .OrderByDescending(p => p.Key.Length).FirstOrDefault();
            int length = match.Key?.Length ?? 1;
            // Treat an item and its following separator as one wrapping unit.
            // A comma must never become a separate node on the next line.
            if (match.Key is not null && length < text.Length && text[length] == ',') {
                length++;
                if (length < text.Length && text[length] == ' ') length++;
            }
            if (match.Key is null) {
                if (first && prefixLength > 0) length = prefixLength;
                else while (length < text.Length && !char.IsWhiteSpace(text[length - 1]) && !links.Keys.Any(k => text.AsSpan(length).StartsWith(k, StringComparison.Ordinal))) length++;
            }
            var node = new LabelTextNode { String = text[..length], FontSize = 14, Height = 24, Width = 1000 };
            if (first && prefixLength > 0) node.TextFlags |= TextFlags.Bold;
            first = false;
            node.Width = Math.Max(1, node.GetTextDrawSize(false).X + 2);
            if (match.Key is not null) {
                node.ItemTooltip = match.Value;
                var normal = new Vector4(0.55f, 0.82f, 1, 1);
                node.TextColor = normal;
                node.ShowClickableCursor = true;
                node.AddEvent(AtkEventType.MouseOver, () => node.TextColor = new Vector4(1, 1, 1, 1));
                node.AddEvent(AtkEventType.MouseOut, () => node.TextColor = normal);
                uint itemId = match.Value;
                node.AddEvent(AtkEventType.MouseDown, () => {
                    var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
                    if (framework != null && (framework->CursorInputs.MouseButtonHeldFlags & FFXIVClientStructs.FFXIV.Client.System.Input.MouseButtonFlags.RBUTTON) != 0) Plugin.RequestItemMenu(itemId);
                });
            }
            node.AttachNode(this);
            segments.Add(node);
            text = text[length..];
        }
        Width = 450;
        Layout();
    }
    protected override void OnSizeChanged() { base.OnSizeChanged(); Layout(); }
    private void Layout()
    {
        if (segments is null || layingOut) return;
        layingOut = true;
        float x = 0, y = 0;
        foreach (var node in segments) {
            if (x > 0 && x + node.Width > Width) { x = 0; y += 24; }
            node.Position = new Vector2(x, y);
            x += node.Width;
        }
        Height = y + 24;
        layingOut = false;
    }
}
