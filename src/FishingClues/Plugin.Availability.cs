using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace FishingClues;

public sealed record FishAvailabilityInfo(bool AvailableNow, string BadgeText, string Tooltip);

public sealed partial class Plugin
{
    private const int MaxAvailabilityWindowsToScan = 216; // ~3.5 real days of weather windows.

    private FishAvailabilityInfo? GetFishAvailability(JournalFish fish)
    {
        if (!data.Fish.TryGetValue(fish.ItemId, out FishCondition? condition) || !condition.RequirementsKnown)
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
            if (territoryId is not uint tId || !DataManager.GetExcelSheet<TerritoryType>().TryGetRow(tId, out TerritoryType territory) || territory.WeatherRate.RowId == 0)
                return null; // No known location, or that location has no dynamic weather; cannot determine.
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
            string current = weatherGated ? FormatWeather(condition.Weather, "any weather") : "any weather";
            return prevWeatherGated ? $"{current} after {FormatWeather(condition.PreviousWeather, "any weather")}" : current;
        }

        if (timeOk && weatherOk && prevOk)
        {
            var met = new List<string>();
            if (timeGated) met.Add(FormatTime(condition.StartHour, condition.EndHour));
            if (WeatherClause() is string metWeather) met.Add(metWeather);
            string availableTooltip = met.Count == 0 ? "Now available." : $"Now available due to {string.Join(" and ", met)}.";
            return new FishAvailabilityInfo(true, "Up now", availableTooltip);
        }

        var waitingFor = new List<string>();
        if (timeGated) waitingFor.Add(FormatTime(condition.StartHour, condition.EndHour));
        if (WeatherClause() is string waitingWeather) waitingFor.Add(waitingWeather);
        string waitingText = waitingFor.Count == 0 ? "conditions to line up" : string.Join(" and ", waitingFor);
        string tooltip = $"Waiting for {waitingText}.";

        long? next = FindNextAvailability(condition, weatherRateId, weatherGated, prevWeatherGated, now);
        if (next is long nextTime)
        {
            TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, nextTime - now));
            return new FishAvailabilityInfo(false, FormatCountdown(span), tooltip);
        }
        return new FishAvailabilityInfo(false, "Not soon", tooltip);
    }

    private uint? ResolveTerritoryId(JournalFish fish)
    {
        if (fish.SpotId != 0)
        {
            JournalSpot? spot = GetJournal().SelectMany(r => r.Areas).SelectMany(a => a.Spots).FirstOrDefault(s => s.Id == fish.SpotId);
            if (spot is not null) return spot.TerritoryId;
        }
        FishLocation? location = data.Locations.FirstOrDefault(l => l.ItemId == fish.ItemId);
        if (location is null) return null;
        foreach (TerritoryType territory in DataManager.GetExcelSheet<TerritoryType>())
            if (territory.Map.RowId == location.MapId) return territory.RowId;
        return null;
    }

    private uint? GetWeatherId(uint weatherRateId, byte target)
    {
        if (weatherRateId == 0 || !DataManager.GetExcelSheet<WeatherRate>().TryGetRow(weatherRateId, out WeatherRate row))
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

    private long? FindNextAvailability(FishCondition condition, uint weatherRateId, bool weatherGated, bool prevWeatherGated, long now)
    {
        long windowStart = EorzeaWeather.WindowStart(now);
        for (int i = 0; i < MaxAvailabilityWindowsToScan; i++)
        {
            long ws = windowStart + i * EorzeaWeather.SecondsPerWeatherWindow;
            bool weatherOk = !weatherGated || (GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(ws)) is uint w && condition.Weather.Contains(w));
            if (!weatherOk) continue;
            bool prevOk = !prevWeatherGated || (GetWeatherId(weatherRateId, EorzeaWeather.CalculateTarget(ws - EorzeaWeather.SecondsPerWeatherWindow)) is uint pw && condition.PreviousWeather.Contains(pw));
            if (!prevOk) continue;
            long? match = FirstTimeMatchInWindow(ws, condition.StartHour, condition.EndHour, i == 0 ? now : ws);
            if (match is long t) return t;
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

    private static string FormatCountdown(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalHours < 1) return $"{span.Minutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }
}
