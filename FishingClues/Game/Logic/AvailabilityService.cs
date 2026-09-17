using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Models;

namespace FishingClues.Game.Logic;

public sealed record FishAvailabilityInfo(bool AvailableNow, string BadgeText, string Tooltip);

// Turns a fish's time/weather requirements into the "available now" or
// "waiting for..." badge shown next to it in the journal.
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
        if (!timeGated && !weatherGated && !prevWeatherGated)
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

        string? WeatherClause()
        {
            if (!weatherGated && !prevWeatherGated) return null;
            string current = weatherGated ? formatter.FormatWeather(condition.Weather, "any weather") : "any weather";
            return prevWeatherGated ? $"{current} after {formatter.FormatWeather(condition.PreviousWeather, "any weather")}" : current;
        }

        if (timeOk && weatherOk && prevOk)
        {
            var met = new List<string>();
            if (timeGated) met.Add(formatter.FormatTime(condition.StartHour, condition.EndHour));
            if (WeatherClause() is string metWeather) met.Add(metWeather);
            string availableTooltip = met.Count == 0 ? "Now available." : $"Now available due to {string.Join(" and ", met)}.";

            if (configuration.DisableAvailabilityCountdown)
                return new FishAvailabilityInfo(true, availableTooltip, availableTooltip);

            long? end = FindAvailabilityEnd(condition, weatherRateId, weatherGated, prevWeatherGated, timeGated, now);
            string availableCountdown = end is long endTime ? FormatCountdown(TimeSpan.FromSeconds(Math.Max(0, endTime - now))) : "Up now";
            return new FishAvailabilityInfo(true, $"{availableCountdown} | {availableTooltip}", availableTooltip);
        }

        var waitingFor = new List<string>();
        if (timeGated) waitingFor.Add(formatter.FormatTime(condition.StartHour, condition.EndHour));
        if (WeatherClause() is string waitingWeather) waitingFor.Add(waitingWeather);
        string waitingText = waitingFor.Count == 0 ? "conditions to line up" : string.Join(" and ", waitingFor);
        string tooltip = $"Waiting for {waitingText}.";

        if (configuration.DisableAvailabilityCountdown)
            return new FishAvailabilityInfo(false, tooltip, tooltip);

        long? next = FindNextAvailability(condition, weatherRateId, weatherGated, prevWeatherGated, now);
        string waitingCountdown = next is long nextTime ? FormatCountdown(TimeSpan.FromSeconds(Math.Max(0, nextTime - now))) : "Not soon";
        return new FishAvailabilityInfo(false, $"{waitingCountdown} | {tooltip}", tooltip);
    }

    private uint? ResolveTerritoryId(JournalFish fish)
    {
        if (fish.SpotId != 0)
        {
            JournalSpot? spot = journal.GetJournal().SelectMany(r => r.Areas).SelectMany(a => a.Spots).FirstOrDefault(s => s.Id == fish.SpotId);
            if (spot is not null) return spot.TerritoryId;
        }
        FishLocation? location = fishData.Data.Locations.FirstOrDefault(l => l.ItemId == fish.ItemId);
        if (location is null) return null;
        foreach (TerritoryType territory in Services.DataManager.GetExcelSheet<TerritoryType>())
            if (territory.Map.RowId == location.MapId) return territory.RowId;
        return null;
    }

    private static uint? GetWeatherId(uint weatherRateId, byte target)
    {
        if (weatherRateId == 0 || !Services.DataManager.GetExcelSheet<WeatherRate>().TryGetRow(weatherRateId, out WeatherRate row))
            return null;
        byte cumulative = 0;
        foreach (var (rate, weather) in row.Rate.Zip(row.Weather))
        {
            if (rate == 0) continue;
            cumulative += rate;
            if (cumulative > target) return weather.RowId;
        }
        return null;
    }

    private static bool WeatherMatchesAt(FishCondition condition, uint weatherRateId, bool weatherGated, bool prevWeatherGated, long ws)
    {
        bool weatherOk = !weatherGated || (GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(ws)) is uint w && condition.Weather.Contains(w));
        if (!weatherOk) return false;
        return !prevWeatherGated || (GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(ws - EorzeaWeather.SecondsPerWeatherWindow)) is uint pw && condition.PreviousWeather.Contains(pw));
    }

    private static long? FindNextAvailability(FishCondition condition, uint weatherRateId, bool weatherGated, bool prevWeatherGated, long now)
    {
        long windowStart = EorzeaWeather.WindowStart(now);
        for (int i = 0; i < MaxAvailabilityWindowsToScan; i++)
        {
            long ws = windowStart + i * EorzeaWeather.SecondsPerWeatherWindow;
            if (!WeatherMatchesAt(condition, weatherRateId, weatherGated, prevWeatherGated, ws)) continue;
            long? match = FirstTimeMatchInWindow(ws, condition.StartHour, condition.EndHour, i == 0 ? now : ws);
            if (match is long t) return t;
        }
        return null;
    }

    private static long? FindAvailabilityEnd(FishCondition condition, uint weatherRateId, bool weatherGated, bool prevWeatherGated, bool timeGated, long now)
    {
        long windowStart = EorzeaWeather.WindowStart(now);
        for (int i = 0; i < MaxAvailabilityWindowsToScan; i++)
        {
            long ws = windowStart + i * EorzeaWeather.SecondsPerWeatherWindow;
            long windowEnd = ws + EorzeaWeather.SecondsPerWeatherWindow;
            if (!WeatherMatchesAt(condition, weatherRateId, weatherGated, prevWeatherGated, ws))
                return Math.Max(now, ws);
            if (!timeGated)
                continue;
            long? end = TimeWindowEndWithin(ws, condition.StartHour, condition.EndHour, i == 0 ? now : ws, windowEnd);
            if (end is long t) return t;
        }
        return null;
    }

    private static bool InTimeWindow(double hour, double start, double end)
    {
        if (start == 0 && end == 24) return true;
        return start <= end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private static long? FirstTimeMatchInWindow(long windowStart, double startHour, double endHour, long earliestAllowed)
    {
        double bandStart = EorzeaWeather.EorzeaHourOfDay(windowStart);
        double bandEnd = bandStart + 8;
        var candidates = new List<long>();

        void Consider(double s, double e)
        {
            double overlapStart = Math.Max(s, bandStart);
            double overlapEnd = Math.Min(e, bandEnd);
            if (overlapEnd <= overlapStart) return;
            long candidateStart = windowStart + (long)Math.Round((overlapStart - bandStart) * EorzeaWeather.SecondsPerEorzeaHour);
            long candidateEnd = windowStart + (long)Math.Round((overlapEnd - bandStart) * EorzeaWeather.SecondsPerEorzeaHour);
            long effective = Math.Max(candidateStart, earliestAllowed);
            if (effective < candidateEnd) candidates.Add(effective);
        }

        if (startHour == 0 && endHour == 24) candidates.Add(Math.Max(windowStart, earliestAllowed));
        else if (startHour <= endHour) Consider(startHour, endHour);
        else { Consider(startHour, 24); Consider(0, endHour); }

        return candidates.Count == 0 ? null : candidates.Min();
    }

    // A condition's end hour landing exactly on the weather band boundary is only
    // ambiguous when the condition's real end is further out and got clipped to the
    // band - then the caller re-checks weather for the next band before concluding
    // the fish stays available. When the real end hour IS the band boundary, there is
    // nothing left to check and this is the true end, even though it also lands on
    // windowEnd.
    private static long? TimeWindowEndWithin(long windowStart, double startHour, double endHour, long earliestAllowed, long windowEnd)
    {
        if (startHour == 0 && endHour == 24) return null;

        double bandStart = EorzeaWeather.EorzeaHourOfDay(windowStart);
        double bandEnd = bandStart + 8;

        (long End, bool ClippedByBand)? EndOf(double s, double e)
        {
            double overlapStart = Math.Max(s, bandStart);
            double overlapEnd = Math.Min(e, bandEnd);
            if (overlapEnd <= overlapStart) return null;
            long candidateStart = windowStart + (long)Math.Round((overlapStart - bandStart) * EorzeaWeather.SecondsPerEorzeaHour);
            long candidateEnd = windowStart + (long)Math.Round((overlapEnd - bandStart) * EorzeaWeather.SecondsPerEorzeaHour);
            if (candidateStart > earliestAllowed || candidateEnd <= earliestAllowed) return null;
            return (candidateEnd, e > bandEnd);
        }

        var result = startHour <= endHour ? EndOf(startHour, endHour) : EndOf(startHour, 24) ?? EndOf(0, endHour);
        if (result is not (long end, bool clippedByBand)) return null;
        return clippedByBand && end == windowEnd ? null : end;
    }

    private static string FormatCountdown(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalHours < 1) return $"{span.Minutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

}
