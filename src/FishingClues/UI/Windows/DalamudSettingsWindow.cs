using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

using FishingClues.Base;

namespace FishingClues.UI.Windows;

public sealed class DalamudSettingsWindow(Configuration configuration, Action save, Action applyNativeLayout, Action openDiagnostics, Action refreshData, Func<bool> refreshBusy, Func<string> refreshStatus, IKeyState keyState, Action refreshJournalContents)
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
        if (replace) capturingKeybind = false;
        ImGui.BeginDisabled(replace);
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
        ImGui.EndDisabled();
        bool showButton = configuration.ShowOpenNormalLogButton;
        if (ImGui.Checkbox("Show the normal Fishing Log button", ref showButton))
        {
            configuration.ShowOpenNormalLogButton = showButton;
            SaveLayout();
        }
        bool disableCountdown = configuration.DisableAvailabilityCountdown;
        if (ImGui.Checkbox("Disable the availability countdown timer", ref disableCountdown))
        {
            configuration.DisableAvailabilityCountdown = disableCountdown;
            SaveLayout();
        }
        DrawWrappedHint("Disables the timer.");
        bool use12Hour = configuration.Use12HourTime;
        if (ImGui.Checkbox("Show times in 12-hour format", ref use12Hour))
        {
            configuration.Use12HourTime = use12Hour;
            SaveLayout();
        }
        DrawWrappedHint("Applies to catch time windows everywhere they're shown, not just the countdown.");
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
        DrawWrappedHint("Uncheck to resize the fish / details divider directly in the journal or fish search.");
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
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("DEBUG");
        bool revealEverything = configuration.DebugRevealEverything;
        if (ImGui.Checkbox("Show every location and fish as unlocked", ref revealEverything))
        {
            configuration.DebugRevealEverything = revealEverything;
            save();
            refreshJournalContents();
        }
        DrawWrappedHint("Testing only. While on, every fishing hole shows as discovered and every fish as caught; turn it off to go back to your real progress.");
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
        DrawWrappedHint("Only usable while the normal Fishing Log is not being replaced.");
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

    // ImGui.TextDisabled doesn't wrap on its own
    private static void DrawWrappedHint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
