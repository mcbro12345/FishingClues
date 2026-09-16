using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace FishingClues;

public sealed class DalamudJournalWindow(
    Func<IReadOnlyList<JournalRegion>> getRegions,
    Action<JournalFish> openFish,
    Action openNormalLog,
    ITextureProvider textures,
    Configuration configuration,
    Func<JournalFish, FishClueSection> buildDetails,
    Action saveDividers) : Window("Fishing Clues###FishingCluesJournal")
{
    private string? selectedRegion;
    private uint selectedSpot;
    private uint detailsSpot;
    private FishClueSection? selectedDetails;
    private bool resetDetailsScroll;
    private uint selectedFish;

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        IReadOnlyList<JournalRegion> regions = getRegions();
        if (regions.Count == 0)
        {
            ImGui.TextUnformatted("No fishing-log locations are available.");
            return;
        }

        JournalRegion region = regions.FirstOrDefault(r => r.Name == selectedRegion) ?? regions[0];
        selectedRegion = region.Name;
        JournalSpot? spot = region.Areas.SelectMany(a => a.Spots).FirstOrDefault(s => s.IsUnlocked && s.Id == selectedSpot)
            ?? region.Areas.SelectMany(a => a.Spots).FirstOrDefault(s => s.IsUnlocked);
        if (spot is not null && selectedSpot != spot.Id)
            selectedSpot = spot.Id;

        float total = ImGui.GetContentRegionAvail().X;
        float regionWidth = Math.Clamp(configuration.NativeRegionWidth, 130, 280);
        float areaWidth = Math.Clamp(configuration.NativeAreaWidth, 240, Math.Max(240, Math.Min(500, total - regionWidth - 300)));
        DrawRegionPane(regions, regionWidth, spot);
        ImGui.SameLine();
        DrawColumnDivider("region-divider", true, total);
        ImGui.SameLine();
        DrawAreaPane(region, areaWidth);
        ImGui.SameLine();
        DrawColumnDivider("area-divider", false, total);
        ImGui.SameLine();
        DrawFishPane(spot);
    }

    private void DrawColumnDivider(string id, bool region, float total)
    {
        Vector2 position = ImGui.GetCursorScreenPos();
        float height = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        if (configuration.LockJournalDividers) ImGui.Dummy(new Vector2(6, height));
        else
        {
            ImGui.InvisibleButton(id, new Vector2(6, height));
            if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            if (ImGui.IsItemActive())
            {
                float delta = ImGui.GetIO().MouseDelta.X;
                if (region) configuration.NativeRegionWidth = Math.Clamp(configuration.NativeRegionWidth + delta, 130, 280);
                else configuration.NativeAreaWidth = Math.Clamp(configuration.NativeAreaWidth + delta, 240, Math.Max(240, Math.Min(500, total - configuration.NativeRegionWidth - 300)));
            }
            if (ImGui.IsItemDeactivated()) saveDividers();
        }
        ImGui.GetWindowDrawList().AddLine(position + new Vector2(3, 0), position + new Vector2(3, height), ImGui.GetColorU32(ImGuiCol.Separator));
    }

    private void DrawRegionPane(IReadOnlyList<JournalRegion> regions, float width, JournalSpot? spot)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled("REGION");
        float footerHeight = configuration.ShowOpenNormalLogButton ? 42.0f : 0.0f;
        ImGui.BeginChild("regions", new Vector2(width, -footerHeight), true);
        foreach (JournalRegion region in regions)
        {
            string regionLabel = region.IsUnlocked ? region.Name : "???";
            if (!ImGui.Selectable($"{regionLabel}##region-{region.Name}", selectedRegion == region.Name))
                continue;
            selectedRegion = region.Name;
            JournalSpot? first = region.Areas.SelectMany(a => a.Spots).FirstOrDefault(s => s.IsUnlocked);
            selectedSpot = first?.Id ?? 0;
        }
        ImGui.EndChild();
        if (configuration.ShowOpenNormalLogButton)
        {
            if (ImGuiComponents.IconButton("##open-normal-fishing-log", FontAwesomeIcon.BookOpen))
                openNormalLog();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Open the normal Fishing Log");
        }
        ImGui.EndGroup();
    }

    private void DrawAreaPane(JournalRegion region, float width)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled("AREA");
        ImGui.BeginChild("areas", new Vector2(width, 0), true);
        foreach (JournalArea area in region.Areas)
        {
            string areaLabel = region.IsUnlocked ? area.Name : "???";
            if (!ImGui.CollapsingHeader($"{areaLabel}##area-{area.Name}"))
                continue;
            foreach (JournalSpot spot in area.Spots)
            {
                string spotLabel = spot.IsUnlocked ? spot.Name : "Undiscovered";
                if (!ImGui.Selectable($"  {spotLabel}##spot-{spot.Id}", selectedSpot == spot.Id,
                        spot.IsUnlocked ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled))
                    continue;
                selectedSpot = spot.Id;
            }
        }
        ImGui.EndChild();
        ImGui.EndGroup();
    }

    private void DrawFishPane(JournalSpot? spot)
    {
        if (detailsSpot != (spot?.Id ?? 0) || !configuration.EmbedFishDetails)
        {
            selectedDetails = null;
            if (detailsSpot != (spot?.Id ?? 0)) selectedFish = 0;
            detailsSpot = spot?.Id ?? 0;
        }
        ImGui.BeginGroup();
        ImGui.TextDisabled("FISH");
        if (spot is null)
            ImGui.TextUnformatted("Select a fishing hole.");
        else
        {
            ImGui.TextUnformatted(spot.Name);
            ImGui.TextDisabled($"Caught {spot.CaughtCount}/{spot.Fish.Count}  •  {spot.MissingCount} remaining");
        }
        ImGui.Separator();
        float available = ImGui.GetContentRegionAvail().Y;
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float body = Math.Max(1, available - 8 - spacing * 2);
        float minimum = Math.Min(80, body / 3);
        float panelHeight = Math.Clamp(body * configuration.DetailsHeightRatio, minimum, body - minimum);
        ImGui.BeginChild("fish", configuration.EmbedFishDetails ? new Vector2(0, Math.Max(1, body - panelHeight)) : Vector2.Zero, true);
        if (spot is not null)
        {
            var caught = spot.Fish.Where(f => f.IsCaught).ToArray();
            var missing = spot.Fish.Where(f => !f.IsCaught).ToArray();
            bool missingFirst = configuration.UncaughtFishFirst && missing.Length > 0;
            if (missingFirst) DrawFishGroup("NOT CAUGHT", missing, false, false);
            DrawFishGroup("CAUGHT", caught, true, missingFirst);
            if (!missingFirst && missing.Length > 0) DrawFishGroup("NOT CAUGHT", missing, false, true);
        }
        ImGui.EndChild();
        if (configuration.EmbedFishDetails)
        {
            Vector2 dividerPosition = ImGui.GetCursorScreenPos();
            float dividerWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
            if (configuration.LockJournalDividers) ImGui.Dummy(new Vector2(dividerWidth, 8));
            else
            {
                ImGui.InvisibleButton("details-divider", new Vector2(dividerWidth, 8));
                if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
                if (ImGui.IsItemActive())
                    configuration.DetailsHeightRatio = Math.Clamp(configuration.DetailsHeightRatio - ImGui.GetIO().MouseDelta.Y / body, minimum / body, 1 - minimum / body);
                if (ImGui.IsItemDeactivated()) saveDividers();
            }
            ImGui.GetWindowDrawList().AddLine(dividerPosition + new Vector2(0, 4), dividerPosition + new Vector2(dividerWidth, 4), ImGui.GetColorU32(ImGuiCol.Separator));
            ImGui.BeginChild("embedded-details", new Vector2(0, panelHeight), true);
            if (resetDetailsScroll) { ImGui.SetScrollY(0); resetDetailsScroll = false; }
            if (selectedDetails is { } section)
            {
                ImGui.TextWrapped(section.Heading);
                ImGui.Separator();
                foreach (string line in section.Lines) ImGui.TextWrapped(line);
            }
            else
            {
                const string hint = "Click a fish to see catch details";
                Vector2 space = ImGui.GetContentRegionAvail();
                Vector2 textSize = ImGui.CalcTextSize(hint);
                ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(Math.Max(0, (space.X - textSize.X) / 2), Math.Max(0, (space.Y - textSize.Y) / 2)));
                ImGui.TextDisabled(hint);
            }
            ImGui.EndChild();
        }
        ImGui.EndGroup();
    }

    private void DrawFishGroup(string heading, IReadOnlyList<JournalFish> fish, bool revealNames, bool separator)
    {
        ImGui.Spacing();
        if (separator) ImGui.Separator();
        ImGui.TextDisabled($"{heading}  {fish.Count}");
        for (int i = 0; i < fish.Count; i++)
        {
            JournalFish entry = fish[i];
            string name = revealNames ? entry.Name : $"???? #{i + 1}";
            bool clicked = false;
            ImGui.PushID((int)entry.FishParameterId);
            if (entry.IsCaught && entry.IconId != 0)
            {
                var wrap = textures.GetFromGameIcon(new GameIconLookup(entry.IconId)).GetWrapOrDefault();
                if (wrap is not null)
                {
                    ImGui.Image(wrap.Handle, new Vector2(36, 36));
                    clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
                }
                else clicked = ImGui.Button("?", new Vector2(36, 36));
            }
            else clicked = ImGui.Button("?", new Vector2(36, 36));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(entry.IsCaught ? entry.Name : "Unknown fish");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            clicked |= ImGui.Selectable($"{name}   Lv. {entry.Level}", selectedFish == entry.FishParameterId, ImGuiSelectableFlags.None, new Vector2(0, 36));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(entry.IsCaught ? entry.Name : "Unknown fish");
                ImGui.EndTooltip();
            }
            ImGui.PopID();
            if (clicked)
            {
                selectedFish = entry.FishParameterId;
                if (configuration.EmbedFishDetails)
                {
                    selectedDetails = buildDetails(entry);
                    resetDetailsScroll = true;
                }
                else openFish(entry);
            }
        }
        if (fish.Count == 0) ImGui.TextDisabled("None");
    }
}
