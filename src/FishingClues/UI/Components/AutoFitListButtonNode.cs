using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace FishingClues.UI.Components;

// A ListButtonNode whose label shrinks to fit its width instead of being
// ellipsized. Used for entries like region names, where cutting the text off
// (e.g. "The World Unsund...") can make the entry impossible to identify.
public sealed class AutoFitListButtonNode : ListButtonNode
{
    private const uint MaxFontSize = 14;
    private const uint MinFontSize = 10;

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();
        float available = Math.Max(10.0f, Width - 10.0f);
        LabelNode.TextFlags = TextFlags.Ellipsis;
        uint fontSize = MaxFontSize;
        LabelNode.FontSize = fontSize;
        while (fontSize > MinFontSize && LabelNode.GetTextDrawSize(false).X > available)
        {
            fontSize--;
            LabelNode.FontSize = fontSize;
        }
    }
}
