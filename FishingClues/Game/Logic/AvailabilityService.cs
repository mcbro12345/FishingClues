using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Models;

namespace FishingClues.Game.Logic;

public sealed record FishAvailabilityInfo(bool AvailableNow, string BadgeText, string Tooltip);

// builds the available/waiting badge for a fish
public sealed class AvailabilityService
{
    private const int MaxAvailabilityWindowsToScan = 216; // ~3.5 real days of weather windows.

    private readonly FishDataService fishData;
    private readonly JournalBuilder journal;
    private readonly FishDetailsFormatter formatter;
    private readonly Configuration configuration;

    public AvailabilityService(FishDataService fishData, JournalBuilder journal, FishDetailsFormatter formatter, Configuration configuration)
    {
        this.fishData = fishData;
        this.journal = journal;
        this.formatter = formatter;
        this.configuration = configuration;
    }

    public FishAvailabilityInfo? GetAvailability(JournalFish fish)
    {
        if (!fishData.Data.Fish.TryGetValue(fish.ItemId, out FishCondition? condition) || !condition.RequirementsKnown)
            return null;
        bool timeGated = condition.StartHour != 0 || condition.EndHour != 24;
        bool weatherGated = condition.Weather.Count > 0;
        bool prevWeatherGated = condition.PreviousWeather.Count > 0;
        bool intuitionGated = condition.IntuitionSeconds is int intuitionWindow && intuitionWindow > 0
            && condition.Predators.Any(p => p.Count >= 2);
        if (!timeGated && !weatherGated && !prevWeatherGated && !intuitionGated)
            return null;

        uint weatherRateId = 0;
        if (weatherGated || prevWeatherGated)
        {
            uint? territoryId = ResolveTerritoryId(fish);
            if (territoryId is not uint tId || !Services.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(tId, out TerritoryType territory) || territory.WeatherRate.RowId == 0)
                return null;
            weatherRateId = territory.WeatherRate.RowId;
        }

        long now = EorzeaWeather.UtcNowSeconds();
        double nowHour = EorzeaWeather.EorzeaHourOfDay(now);
        bool timeOk = !timeGated || InTimeWindow(nowHour, condition.StartHour, condition.EndHour);
        uint? currentWeather = (weatherGated || prevWeatherGated) ? GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(now)) : null;
        bool weatherOk = !weatherGated || (currentWeather is uint cw && condition.Weather.Contains(cw));
        uint? prevWeather = prevWeatherGated ? GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(EorzeaWeather.WindowStart(now) - EorzeaWeather.SecondsPerWeatherWindow)) : null;
        bool prevOk = !prevWeatherGated || (prevWeather is uint pw && condition.PreviousWeather.Contains(pw));
        // player-triggered, read fresh each call
        IntuitionStatus? intuition = intuitionGated ? IntuitionService.GetActive() : null;
        bool intuitionOk = !intuitionGated || IsMatchingIntuition(fish, intuition);

        if (timeOk && weatherOk && prevOk && intuitionOk)
        {
            string availableTooltip = !intuitionGated
                ? formatter.AvailableSentence(condition)
                : FishDetailsFormatter.IsGated(condition)
                    ? $"Intuition catch window is open. {formatter.AvailableSentence(condition)}"
                    : "Intuition catch window is open.";

            if (configuration.DisableAvailabilityCountdown)
                return new FishAvailabilityInfo(true, availableTooltip, availableTooltip);

            string availableCountdown;
            if (intuitionGated)
                availableCountdown = $"Ends in {FormatCountdown(TimeSpan.FromSeconds(Math.Max(0, intuition!.RemainingSeconds)))}";
            else
            {
                long? end = FindAvailabilityEnd(condition, weatherRateId, weatherGated, prevWeatherGated, timeGated, now);
                availableCountdown = end is long endTime
                    ? $"Ends in {FormatCountdown(TimeSpan.FromSeconds(Math.Max(0, endTime - now)))}"
                    : "Not ending soon";
            }
            return new FishAvailabilityInfo(true, $"{availableCountdown} | {availableTooltip}", availableTooltip);
        }

        // intuition has no start time, so say what to do instead
        string tooltip = intuitionGated && !intuitionOk
            ? (timeOk && weatherOk && prevOk ? formatter.IntuitionSentence(condition, fish.SpotId) : $"{formatter.WaitingSentence(condition)} {formatter.IntuitionSentence(condition, fish.SpotId)}")
            : formatter.WaitingSentence(condition);

        if (configuration.DisableAvailabilityCountdown || (intuitionGated && !intuitionOk))
            return new FishAvailabilityInfo(false, tooltip, tooltip);

        long? next = FindNextAvailability(condition, weatherRateId, weatherGated, prevWeatherGated, now);
        string waitingCountdown = next is long nextTime
            ? $"Starts in {FormatCountdown(TimeSpan.FromSeconds(Math.Max(0, nextTime - now)))}"
            : "Not starting soon";
        return new FishAvailabilityInfo(false, $"{waitingCountdown} | {tooltip}", tooltip);
    }

    // matches the live Intuition buff to a fish. Param is assumed to be its FishParameter/item id (unverified), otherwise accept it if it's the only intuition fish at the hole
    private bool IsMatchingIntuition(JournalFish fish, IntuitionStatus? intuition)
    {
        if (intuition is null) return false;
        if (intuition.Param == fish.FishParameterId || intuition.Param == fish.ItemId) return true;
        if (fish.SpotId == 0) return false;
        JournalSpot? spot = journal.FindSpot(fish.SpotId);
        if (spot is null) return false;
        int intuitionFishAtSpot = spot.Fish.Count(f => fishData.Data.Fish.TryGetValue(f.ItemId, out var c)
            && c.IntuitionSeconds is int seconds && seconds > 0 && c.Predators.Any(p => p.Count >= 2));
        return intuitionFishAtSpot == 1;
    }

    private uint? ResolveTerritoryId(JournalFish fish)
    {
        if (fish.SpotId != 0)
        {
            JournalSpot? spot = journal.FindSpot(fish.SpotId);
            if (spot is not null) return spot.TerritoryId;
        }
        FishLocation? location = fishData.Data.Locations.FirstOrDefault(l => l.ItemId == fish.ItemId);
        if (location is null) return null;
        foreach (TerritoryType territory in Services.DataManager.GetExcelSheet<TerritoryType>())
            if (territory.Map.RowId == location.MapId) return territory.RowId;
        return null;
    }

    // weather for each 0-99 target of a zone's rate table, built once per zone
    private static readonly Dictionary<uint, uint?[]> WeatherTables = new();

    private static uint? GetWeatherId(uint weatherRateId, byte target)
    {
        if (weatherRateId == 0) return null;
        if (!WeatherTables.TryGetValue(weatherRateId, out var table))
            WeatherTables[weatherRateId] = table = BuildWeatherTable(weatherRateId);
        return target < table.Length ? table[target] : null;
    }

    private static uint?[] BuildWeatherTable(uint weatherRateId)
    {
        var table = new uint?[100];
        if (!Services.DataManager.GetExcelSheet<WeatherRate>().TryGetRow(weatherRateId, out WeatherRate row)) return table;
        byte cumulative = 0;
        int target = 0;
        foreach (var (rate, weather) in row.Rate.Zip(row.Weather))
        {
            if (rate == 0) continue;
            cumulative += rate;
            for (; target < cumulative && target < table.Length; target++) table[target] = weather.RowId;
        }
        return table;
    }

    private static bool WeatherMatchesAt(FishCondition condition, uint weatherRateId, bool weatherGated, bool prevWeatherGated, long ws)
    {
        bool weatherOk = !weatherGated || (GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(ws)) is uint w && condition.Weather.Contains(w));
        if (!weatherOk) return false;
        return !prevWeatherGated || (GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(ws - EorzeaWeather.SecondsPerWeatherWindow)) is uint pw && condition.PreviousWeather.Contains(pw));
    }

    // the next weather windows from now: where each starts, and the earliest time in it that counts
    private static IEnumerable<(long Start, long Earliest)> UpcomingWindows(long now)
    {
        long first = EorzeaWeather.WindowStart(now);
        for (int i = 0; i < MaxAvailabilityWindowsToScan; i++)
        {
            long start = first + i * EorzeaWeather.SecondsPerWeatherWindow;
            yield return (start, i == 0 ? now : start);
        }
    }

    private static long? FindNextAvailability(FishCondition condition, uint weatherRateId, bool weatherGated, bool prevWeatherGated, long now)
    {
        foreach (var (ws, earliest) in UpcomingWindows(now))
        {
            if (!WeatherMatchesAt(condition, weatherRateId, weatherGated, prevWeatherGated, ws)) continue;
            if (FirstTimeMatchInWindow(ws, condition.StartHour, condition.EndHour, earliest) is long t) return t;
        }
        return null;
    }

    private static long? FindAvailabilityEnd(FishCondition condition, uint weatherRateId, bool weatherGated, bool prevWeatherGated, bool timeGated, long now)
    {
        foreach (var (ws, earliest) in UpcomingWindows(now))
        {
            if (!WeatherMatchesAt(condition, weatherRateId, weatherGated, prevWeatherGated, ws))
                return Math.Max(now, ws);
            if (!timeGated) continue;
            if (TimeWindowEndWithin(ws, condition.StartHour, condition.EndHour, earliest, ws + EorzeaWeather.SecondsPerWeatherWindow) is long t) return t;
        }
        return null;
    }
    private static bool InTimeWindow(double hour, double start, double end)
    {
        if (start == 0 && end == 24) return true;
        return start <= end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    // an hour range clipped to the weather band that starts at windowStart, as unix seconds.
    // null if the range doesn't touch the band
    private static (long Start, long End)? ClipToBand(long windowStart, double startHour, double endHour)
    {
        double bandStart = EorzeaWeather.EorzeaHourOfDay(windowStart);
        double from = Math.Max(startHour, bandStart), to = Math.Min(endHour, bandStart + 8);
        if (to <= from) return null;
        return (windowStart + (long)Math.Round((from - bandStart) * EorzeaWeather.SecondsPerEorzeaHour),
            windowStart + (long)Math.Round((to - bandStart) * EorzeaWeather.SecondsPerEorzeaHour));
    }

    private static long? FirstTimeMatchInWindow(long windowStart, double startHour, double endHour, long earliestAllowed)
    {
        var candidates = new List<long>();

        void Consider(double s, double e)
        {
            if (ClipToBand(windowStart, s, e) is not (long start, long end)) return;
            long effective = Math.Max(start, earliestAllowed);
            if (effective < end) candidates.Add(effective);
        }

        if (startHour == 0 && endHour == 24) candidates.Add(Math.Max(windowStart, earliestAllowed));
        else if (startHour <= endHour) Consider(startHour, endHour);
        else { Consider(startHour, 24); Consider(0, endHour); }

        return candidates.Count == 0 ? null : candidates.Min();
    }

    // an end hour on the band boundary only counts as the end if the band didn't clip it
    private static long? TimeWindowEndWithin(long windowStart, double startHour, double endHour, long earliestAllowed, long windowEnd)
    {
        if (startHour == 0 && endHour == 24) return null;

        double bandEnd = EorzeaWeather.EorzeaHourOfDay(windowStart) + 8;

        (long End, bool ClippedByBand)? EndOf(double s, double e)
        {
            if (ClipToBand(windowStart, s, e) is not (long start, long end)) return null;
            if (start > earliestAllowed || end <= earliestAllowed) return null;
            return (end, e > bandEnd);
        }

        bool wrapsPastMidnight = startHour > endHour;
        var evening = wrapsPastMidnight ? EndOf(startHour, 24) : null;
        var result = wrapsPastMidnight ? evening ?? EndOf(0, endHour) : EndOf(startHour, endHour);
        if (result is not (long end, bool clippedByBand)) return null;
        // 9pm-3am carries into the next day's first band
        bool carriesPastMidnight = evening is not null && endHour > 0 && end == windowEnd;
        return (clippedByBand || carriesPastMidnight) && end == windowEnd ? null : end;
    }
    // "45s", "12m 05s", "1h 53m 12s": leading zero units dropped, seconds rounded up
    private static string FormatCountdown(TimeSpan span)
    {
        long total = (long)Math.Ceiling(Math.Max(0.0, span.TotalSeconds));
        long days = total / 86400, hours = total % 86400 / 3600, minutes = total % 3600 / 60, seconds = total % 60;
        if (days > 0) return $"{days}d {hours}h {minutes:00}m {seconds:00}s";
        if (hours > 0) return $"{hours}h {minutes:00}m {seconds:00}s";
        if (minutes > 0) return $"{minutes}m {seconds:00}s";
        return $"{seconds}s";
    }
}
