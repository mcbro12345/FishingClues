using System;

namespace FishingClues.Game.Data;

/// <summary>
/// Reproduces the game's real-time-to-Eorzea-time conversion and the weather
/// forecast algorithm used to determine a zone's weather at a given moment.
/// Computed entirely locally; no external data source is required.
/// </summary>
public static class EorzeaWeather
{
    public const long SecondsPerEorzeaHour = 175;
    public const long SecondsPerEorzeaDay = SecondsPerEorzeaHour * 24;
    public const long SecondsPerWeatherWindow = SecondsPerEorzeaHour * 8;

    /// <summary>The 0-99 target value used to pick a weather from a zone's cumulative rate table.</summary>
    public static byte CalculateTarget(long unixSeconds)
    {
        long hour = unixSeconds / SecondsPerEorzeaHour;
        uint shiftedHour = (uint)((hour + 8 - (hour % 8)) % 24);
        long day = unixSeconds / SecondsPerEorzeaDay;
        uint calc = (uint)day * 100 + shiftedHour;
        calc = (calc << 11) ^ calc;
        calc = (calc >> 8) ^ calc;
        return (byte)(calc % 100);
    }

    /// <summary>The real-time (unix seconds) start of the 1400-second weather window containing the given time.</summary>
    public static long WindowStart(long unixSeconds)
    {
        long remainder = unixSeconds % SecondsPerWeatherWindow;
        if (remainder < 0) remainder += SecondsPerWeatherWindow;
        return unixSeconds - remainder;
    }

    /// <summary>The current Eorzea hour-of-day (0-24, exclusive) at the given real time.</summary>
    public static double EorzeaHourOfDay(long unixSeconds)
    {
        long remainder = unixSeconds % SecondsPerEorzeaDay;
        if (remainder < 0) remainder += SecondsPerEorzeaDay;
        return remainder / (double)SecondsPerEorzeaHour;
    }

    public static long UtcNowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
