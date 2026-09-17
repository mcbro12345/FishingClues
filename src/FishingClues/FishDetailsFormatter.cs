using System;
using System.Collections.Generic;
using System.Linq;
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace FishingClues;

// Turns a fish's raw catch-condition data into the bait/time/weather lines
// shown in the clue popup, the journal's detail panel, and the guide.
public sealed class FishDetailsFormatter
{
    private readonly FishDataService fishData;
    private readonly Configuration configuration;

    public FishDetailsFormatter(FishDataService fishData, Configuration configuration)
    {
        this.fishData = fishData;
        this.configuration = configuration;
    }

    public FishClueSection BuildFishSection(JournalFish fish)
    {
        var lines = new List<string>();
        if (fish.IdentityVisible && (fishData.Data.Info.GetValueOrDefault(fish.ItemId) ?? fish.Info) is { } info)
        {
            if (!string.IsNullOrWhiteSpace(info.Waters)) lines.Add($"Waters: {info.Waters}");
            if (!string.IsNullOrWhiteSpace(info.Region) || !string.IsNullOrWhiteSpace(info.Zone)) lines.Add($"Location: {info.Region} - {info.Zone}");
            if (info.Collectable) lines.Add("Collectable: Yes");
            if (!string.IsNullOrWhiteSpace(info.Description)) lines.Add($"Description: {info.Description}");
        }
        lines.AddRange(BuildRequirementLines(fish.ItemId, fish.SpotId));
        string heading = fish.IdentityVisible ? fish.Name : "????";
        if (fish.Level > 0) heading += $"   Lv. {fish.Level}";
        return new FishClueSection(heading, lines);
    }

    public IReadOnlyList<string> BuildRequirementLines(uint itemId, uint spotId = 0)
    {
        FishDataFile data = fishData.Data;
        var lines = new List<string>();
        lines.AddRange(BuildBaitLines(itemId, spotId));
        if (!data.Fish.TryGetValue(itemId, out FishCondition? condition))
        {
            lines.Add("Requirements unknown."); return lines;
        }
        if (!condition.RequirementsKnown) lines.Add("Some requirements are unknown.");
        if (condition.StartHour != 0 || condition.EndHour != 24)
            lines.Add($"Time: {FormatTime(condition.StartHour, condition.EndHour)}");
        if (condition.Weather.Count > 0)
            lines.Add($"Weather: {FormatWeather(condition.Weather, "No special requirement")}");
        if (condition.PreviousWeather.Count > 0) lines.Add($"Previous weather: {FormatWeather(condition.PreviousWeather, "None")}");
        if (condition.RequirementsKnown && condition.StartHour == 0 && condition.EndHour == 24 &&
            condition.Weather.Count == 0 && condition.PreviousWeather.Count == 0 && condition.Predators.Count == 0 &&
            condition.Folklore is null && condition.Snagging != true && string.IsNullOrWhiteSpace(condition.Lure))
            lines.Add("No special requirements.");
        foreach (List<uint> predator in condition.Predators)
            if (predator.Count >= 2) lines.Add($"Intuition: catch {predator[1]} x {ItemName(predator[0])}");
        if (condition.IntuitionSeconds is int seconds && seconds > 0) lines.Add($"Intuition window: {seconds / 60}:{seconds % 60:00}");
        if (condition.Folklore is uint folklore) lines.Add($"Folklore: {data.Folklore.GetValueOrDefault(folklore, $"Book #{folklore}")}");
        if (condition.FishEyes == true) lines.Add("Fish Eyes: supported");
        if (condition.Snagging == true) lines.Add("Snagging: required");
        if (!string.IsNullOrWhiteSpace(condition.Lure)) lines.Add($"Lure: {condition.Lure}");
        if (!string.IsNullOrWhiteSpace(condition.Gig)) lines.Add($"Spear shadow size: {condition.Gig}");
        if (!string.IsNullOrWhiteSpace(condition.SpearSpeed)) lines.Add($"Spear movement speed: {condition.SpearSpeed}");
        if (!string.IsNullOrWhiteSpace(condition.Tug) || !string.IsNullOrWhiteSpace(condition.Hookset)) lines.Add($"Hook: {FormatHook(condition)}");
        return lines;
    }

    private IReadOnlyList<string> BuildBaitLines(uint itemId, uint spotId)
    {
        FishDataFile data = fishData.Data;
        var lines = new List<string>();
        if (data.Locations.Any(l => l.ItemId == itemId && l.Spearfishing) ||
            (data.Fish.TryGetValue(itemId, out var condition) && !string.IsNullOrEmpty(condition.Gig)))
            return ["Method: Spearfishing - no bait required."];
        if (spotId == 0) return [];
        if (!data.SpotBaits.TryGetValue(itemId, out var spots) || !spots.TryGetValue(spotId, out var entry))
            return ["Baits for this fishing hole: unknown."];
        bool IsFish(uint id) => data.Info.ContainsKey(id) || data.Fish.ContainsKey(id);
        var direct = entry.Recommended.Concat(entry.Observed).Distinct().Where(id => !IsFish(id)).ToArray();
        var mooch = entry.Recommended.Concat(entry.Observed).Distinct().Where(IsFish).ToArray();
        var recommended = direct.Where(entry.Recommended.Contains).ToArray();
        var reported = direct.Where(id => !entry.Recommended.Contains(id)).ToArray();
        if (recommended.Length > 0) lines.Add($"Bait: {string.Join(" / ", recommended.Select(ItemName))}");
        if (reported.Length > 0) lines.Add($"Other reported baits: {string.Join(" / ", reported.Select(ItemName))}");
        if (mooch.Length > 0)
        {
            lines.Add($"Mooch from: {string.Join(" / ", mooch.Select(ItemName))}");
            foreach (var id in mooch) AppendMooch(id, spotId, new HashSet<uint> { itemId }, 1, lines);
        }
        if (direct.Length == 0 && mooch.Length == 0) lines.Add("Baits for this fishing hole: unknown.");
        if (entry.MinimumGathering > 0) lines.Add($"Minimum gathering: {entry.MinimumGathering}");

        return lines;
    }

    private void AppendMooch(uint fish, uint spot, HashSet<uint> visited, int depth, List<string> lines)
    {
        FishDataFile data = fishData.Data;
        if (depth > 4 || !visited.Add(fish)) return;
        if (!data.SpotBaits.TryGetValue(fish, out var spots) || !spots.TryGetValue(spot, out var entry))
        {
            lines.Add($"To catch {ItemName(fish)} here: requirements unknown."); return;
        }
        var baits = entry.Recommended.Concat(entry.Observed).Distinct().ToArray();
        lines.Add($"To catch {ItemName(fish)} here: {string.Join(" / ", baits.Select(ItemName))}");
        foreach (var bait in baits.Where(id => data.Info.ContainsKey(id) || data.Fish.ContainsKey(id)))
            AppendMooch(bait, spot, new HashSet<uint>(visited), depth + 1, lines);
    }

    public string ItemName(uint id) => Services.DataManager.GetExcelSheet<ItemSheet>().TryGetRow(id, out var item) ? item.Name.ToString() : fishData.Data.Items.GetValueOrDefault(id, $"Item #{id}");

    public string FormatWeather(List<uint> ids, string fallback) => ids.Count == 0 ? fallback : string.Join(" / ", ids.Select(id => fishData.Data.Weather.GetValueOrDefault(id, $"Weather #{id}")));

    public string FormatTime(double start, double end)
    {
        if (start == 0 && end == 24) return "No special requirement (any time)";
        int startMinutes = (int)Math.Round(start * 60) % 1440;
        int endMinutes = (int)Math.Round(end * 60) % 1440;
        return $"{FormatClock(startMinutes)}-{FormatClock(endMinutes)} ET";
    }

    public string FormatClock(int minutesOfDay)
    {
        int hour = minutesOfDay / 60, minute = minutesOfDay % 60;
        if (!configuration.Use12HourTime) return $"{hour:00}:{minute:00}";
        int hour12 = hour % 12 == 0 ? 12 : hour % 12;
        return $"{hour12}:{minute:00} {(hour < 12 ? "AM" : "PM")}";
    }

    private static string FormatHook(FishCondition condition)
    {
        string marks = condition.Tug?.ToLowerInvariant() switch { "light" => "!", "medium" => "!!", "heavy" or "legendary" => "!!!", _ => "Unknown bite" };
        return string.IsNullOrWhiteSpace(condition.Hookset) ? marks : $"{marks} - {condition.Hookset} Hookset";
    }
}
