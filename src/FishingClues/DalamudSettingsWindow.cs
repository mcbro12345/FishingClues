using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace FishingClues;

public sealed class DalamudSettingsWindow(Configuration configuration, Action save, Action applyNativeLayout, Action openDiagnostics, Action refreshData, Func<bool> refreshBusy, Func<string> refreshStatus, IKeyState keyState)
    : Window("Fishing Clues Settings###FishingCluesSettings")
{
    private bool capturingKeybind;

    public override void PreDraw() => SizeConstraints = new WindowSizeConstraints
    {
        MinimumSize = new Vector2(420, 280),
        MaximumSize = new Vector2(720, 620),
    };

    public override void Draw()
    {
        ImGui.TextDisabled("JOURNAL");
        bool replace = configuration.ReplaceNormalFishingLog;
        if (ImGui.Checkbox("Replace the normal Fishing Log", ref replace))
        {
            configuration.ReplaceNormalFishingLog = replace;
            save();
        }
        bool showButton = configuration.ShowOpenNormalLogButton;
        if (ImGui.Checkbox("Show the normal Fishing Log button", ref showButton))
        {
            configuration.ShowOpenNormalLogButton = showButton;
            SaveLayout();
        }
        if (!replace)
        {
            bool keybindEnabled = configuration.JournalKeybindEnabled;
            if (ImGui.Checkbox("Open the custom journal with a keybind", ref keybindEnabled))
            {
                configuration.JournalKeybindEnabled = keybindEnabled;
                capturingKeybind = false;
                save();
            }
            if (keybindEnabled)
            {
                ImGui.Indent();
                DrawKeybindCapture();
                ImGui.Unindent();
            }
        }
        bool uncaughtFirst = configuration.UncaughtFishFirst;
        if (ImGui.Checkbox("Uncaught fish first", ref uncaughtFirst))
        {
            configuration.UncaughtFishFirst = uncaughtFirst;
            SaveLayout();
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("DIVIDER LOCK");
        bool locked = configuration.LockJournalDividers;
        if (ImGui.Checkbox("Lock the fish / details divider", ref locked))
        {
            configuration.LockJournalDividers = locked;
            SaveLayout();
        }
        ImGui.TextDisabled("Uncheck to resize the fish / details divider directly in the journal or fish search.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("FISHING DATA");
        bool autoRefresh = configuration.AutoRefreshFishData;
        if (ImGui.Checkbox("Check for fishing data updates daily", ref autoRefresh))
        {
            configuration.AutoRefreshFishData = autoRefresh;
            save();
        }
        ImGui.BeginDisabled(refreshBusy());
        if (ImGui.Button("Refresh fishing data now")) refreshData();
        ImGui.EndDisabled();
        ImGui.TextWrapped(refreshStatus());
        ImGui.TextWrapped("Downloads catch conditions from Fish Tracker and GatherBuddy. New discoveries appear when their maintainers publish them.");
        ImGui.Spacing();
        if (ImGui.Button("Open diagnostic report")) openDiagnostics();
    }

    private void DrawKeybindCapture()
    {
        if (capturingKeybind)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Press a key... (Escape to cancel)");
            if (keyState[VirtualKey.ESCAPE])
            {
                capturingKeybind = false;
            }
            else
            {
                foreach (VirtualKey key in keyState.GetValidVirtualKeys())
                {
                    if (key is VirtualKey.CONTROL or VirtualKey.MENU or VirtualKey.SHIFT or VirtualKey.LCONTROL
                        or VirtualKey.RCONTROL or VirtualKey.LMENU or VirtualKey.RMENU or VirtualKey.LSHIFT or VirtualKey.RSHIFT)
                        continue;
                    if (!keyState[key]) continue;
                    configuration.JournalKeybindKey = (int)key;
                    configuration.JournalKeybindCtrl = keyState[VirtualKey.CONTROL];
                    configuration.JournalKeybindAlt = keyState[VirtualKey.MENU];
                    configuration.JournalKeybindShift = keyState[VirtualKey.SHIFT];
                    capturingKeybind = false;
                    save();
                    break;
                }
            }
        }
        else if (ImGui.Button(DescribeKeybind()))
        {
            capturingKeybind = true;
        }
        ImGui.TextDisabled("Only usable while the normal Fishing Log is not being replaced.");
    }

    private string DescribeKeybind()
    {
        if (configuration.JournalKeybindKey == 0) return "Click to set keybind";
        var parts = new System.Collections.Generic.List<string>();
        if (configuration.JournalKeybindCtrl) parts.Add("Ctrl");
        if (configuration.JournalKeybindAlt) parts.Add("Alt");
        if (configuration.JournalKeybindShift) parts.Add("Shift");
        parts.Add(((VirtualKey)configuration.JournalKeybindKey).ToString());
        return string.Join(" + ", parts);
    }

    private void SaveLayout()
    {
        save();
        applyNativeLayout();
    }
}
