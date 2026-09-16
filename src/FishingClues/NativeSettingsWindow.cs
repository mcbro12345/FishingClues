using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace FishingClues;

public sealed class NativeSettingsWindow(Configuration configuration, Action save) : NativeAddon
{
    private VerticalListNode? list;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        list = new VerticalListNode
        {
            Position = ContentStartPosition,
            Size = ContentSize,
            FitWidth = true,
            FitContents = true,
            ItemSpacing = 10.0f,
        };

        list.AddNode(new CategoryTextNode { String = "BEHAVIOR" });
        AddCheckbox("Use the experimental native journal", configuration.UiMode == JournalUiMode.Native, value =>
        {
            configuration.UiMode = value ? JournalUiMode.Native : JournalUiMode.Dalamud;
            save();
        });
        AddCheckbox("Replace the normal Fishing Log", configuration.ReplaceNormalFishingLog, value =>
        {
            configuration.ReplaceNormalFishingLog = value;
            save();
        });
        AddCheckbox("Open clues when clicking an unknown fish in the normal log", configuration.OpenUnknownOnLeftClick, value =>
        {
            configuration.OpenUnknownOnLeftClick = value;
            save();
        });

        list.AddNode(new LabelTextNode
        {
            Height = 58.0f,
            FontSize = 14,
            String = "Dalamud UI is the stable default. Native UI remains available for testing. Replacement uses whichever mode is selected.",
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
        });
        list.AttachNode(this);
    }

    private void AddCheckbox(string text, bool initialValue, Action<bool> changed)
    {
        if (list is null)
            return;
        var checkbox = new CheckboxNode
        {
            Height = 24.0f,
            String = text,
        };
        checkbox.IsChecked = initialValue;
        checkbox.OnClick = changed;
        list.AddNode(checkbox);
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        base.OnFinalize(addon);
        list = null;
    }
}
