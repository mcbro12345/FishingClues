using System;
using System.Collections.Generic;
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
    private const float DefaultRegionWidth = 181.0f;
    private const float DefaultAreaWidth = 298.0f;

    private bool capturingKeybind;

    public override void PreDraw() => SizeConstraints = new WindowSizeConstraints
    {
        MinimumSize = new Vector2(420, 280),
        MaximumSize = new Vector2(720, 620),
    };

    public override void Draw()
    {
        DrawJournalSettings();
        Section("DIVIDER LOCK");
        Toggle("Lock the fish / details divider", configuration.LockJournalDividers, v => configuration.LockJournalDividers = v,
            SaveLayout, "Uncheck to resize the fish / details divider directly in the journal or fish search.");
        Section("FISHING DATA");
        DrawFishingDataSettings();
        Section("DEBUG");
        DrawDebugSettings();
    }

    private void DrawJournalSettings()
    {
        ImGui.TextDisabled("JOURNAL");
        Toggle("Replace the normal Fishing Log", configuration.ReplaceNormalFishingLog, v =>
        {
            configuration.ReplaceNormalFishingLog = v;
            if (v) capturingKeybind = false;
        }, save);
        ImGui.BeginDisabled(configuration.ReplaceNormalFishingLog);
        Toggle("Open the custom journal with a keybind", configuration.JournalKeybindEnabled, v =>
        {
            configuration.JournalKeybindEnabled = v;
            capturingKeybind = false;
        }, save);
        if (configuration.JournalKeybindEnabled)
        {
            ImGui.Indent();
            DrawKeybindCapture();
            ImGui.Unindent();
        }
        ImGui.EndDisabled();
        Toggle("Show the normal Fishing Log button", configuration.ShowOpenNormalLogButton, v => configuration.ShowOpenNormalLogButton = v, SaveLayout);
        Toggle("Show the area location map", configuration.ShowAreaLocationMap, v => configuration.ShowAreaLocationMap = v, SaveLayout,
            "Shows a map under the area list for the area you have open, so you can click a fishing hole directly on it.");
        Toggle("Disable the availability countdown timer", configuration.DisableAvailabilityCountdown, v => configuration.DisableAvailabilityCountdown = v, SaveLayout);
        Toggle("Show times in 12-hour format", configuration.Use12HourTime, v => configuration.Use12HourTime = v, SaveLayout,
            "Applies to catch time windows everywhere they're shown, not just the countdown.");
        Toggle("Uncaught fish first", configuration.UncaughtFishFirst, v => configuration.UncaughtFishFirst = v, SaveLayout);
        Toggle("Sort bait by item level (highest first)", configuration.SortBaitByItemLevel, v => configuration.SortBaitByItemLevel = v, SaveLayout,
            "Orders the bait lists in a fish's details from the highest item level to the lowest. Applies the next time you open a fish's details.");
    }

    private void DrawFishingDataSettings()
    {
        Toggle("Check for fishing data updates daily", configuration.AutoRefreshFishData, v => configuration.AutoRefreshFishData = v, save);
        ImGui.BeginDisabled(refreshBusy());
        if (ImGui.Button("Refresh fishing data now")) refreshData();
        ImGui.EndDisabled();
        ImGui.TextWrapped(refreshStatus());
        ImGui.TextWrapped("Downloads catch conditions from Fish Tracker and GatherBuddy. New discoveries appear when their maintainers publish them.");
    }

    private void DrawDebugSettings()
    {
        if (ImGui.Button("Open diagnostic report")) openDiagnostics();
        ImGui.Spacing();
        Toggle("Debug Mode", configuration.DebugMode, v => configuration.DebugMode = v, save);
        if (!configuration.DebugMode) return;

        ImGui.Indent();
        Toggle("Show every location and fish as unlocked", configuration.DebugRevealEverything, v => configuration.DebugRevealEverything = v,
            () =>
            {
                save();
                refreshJournalContents();
            },
            "Testing only. While on, every fishing hole shows as discovered and every fish as caught; turn it off to go back to your real progress.");

        ImGui.Spacing();
        Toggle("Unlock the region column divider", !configuration.LockRegionDivider, v => configuration.LockRegionDivider = !v, SaveLayout);
        if (ImGui.Button("Restore default region divider position##RegionWidthDefault"))
        {
            configuration.NativeRegionWidth = DefaultRegionWidth;
            SaveLayout();
        }
        Toggle("Unlock the area column divider", !configuration.LockAreaDivider, v => configuration.LockAreaDivider = !v, SaveLayout);
        if (ImGui.Button("Restore default area divider position##AreaWidthDefault"))
        {
            configuration.NativeAreaWidth = DefaultAreaWidth;
            SaveLayout();
        }
        DrawWrappedHint("Lets you drag the region and area column edges directly in the journal, the same way the fish / details divider already works.");
        ImGui.Unindent();
    }

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled(title);
    }

    // A checkbox that applies its new value and then commits it (save, or save and re-lay out the journal).
    private static void Toggle(string label, bool current, Action<bool> apply, Action commit, string? hint = null)
    {
        bool value = current;
        if (ImGui.Checkbox(label, ref value))
        {
            apply(value);
            commit();
        }
        if (hint is not null) DrawWrappedHint(hint);
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
        var parts = new List<string>();
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
