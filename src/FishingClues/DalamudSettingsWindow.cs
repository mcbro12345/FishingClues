using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FishingClues;

public sealed class DalamudSettingsWindow(Configuration configuration, Action save, Action applyNativeLayout, Action openDiagnostics, Action refreshData, Func<bool> refreshBusy, Func<string> refreshStatus)
    : Window("Fishing Clues Settings###FishingCluesSettings")
{
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
        bool uncaughtFirst = configuration.UncaughtFishFirst;
        if (ImGui.Checkbox("Uncaught fish first", ref uncaughtFirst))
        {
            configuration.UncaughtFishFirst = uncaughtFirst;
            SaveLayout();
        }
        bool showButton = configuration.ShowOpenNormalLogButton;
        if (ImGui.Checkbox("Show the normal Fishing Log button", ref showButton))
        {
            configuration.ShowOpenNormalLogButton = showButton;
            SaveLayout();
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("DIVIDER LOCKS");
        bool region = configuration.LockRegionDivider;
        if (ImGui.Checkbox("Lock region / area divider", ref region))
        {
            configuration.LockRegionDivider = region;
            SaveLayout();
        }
        bool area = configuration.LockAreaDivider;
        if (ImGui.Checkbox("Lock area / fish divider", ref area))
        {
            configuration.LockAreaDivider = area;
            SaveLayout();
        }
        bool details = configuration.LockDetailsDivider;
        if (ImGui.Checkbox("Lock fish / details divider", ref details))
        {
            configuration.LockDetailsDivider = details;
            SaveLayout();
        }
        ImGui.TextDisabled("Uncheck a divider to resize it directly in the journal.");
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

    private void SaveLayout()
    {
        save();
        applyNativeLayout();
    }
}
