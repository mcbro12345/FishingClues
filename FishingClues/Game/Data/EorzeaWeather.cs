using System;

namespace FishingClues.Game.Data;

// real time -> Eorzea time and the weather forecast, all computed locally
public static class EorzeaWeather
{
    public const long SecondsPerEorzeaHour = 175;
    public const long SecondsPerEorzeaDay = SecondsPerEorzeaHour * 24;
    public const long SecondsPerWeatherWindow = SecondsPerEorzeaHour * 8;

    // 0-99 target used to pick a weather from a zone's rate table
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

    // real start (unix seconds) of the 1400s weather window containing the time
    public static long WindowStart(long unixSeconds)
    {
        long remainder = unixSeconds % SecondsPerWeatherWindow;
        if (remainder < 0) remainder += SecondsPerWeatherWindow;
        return unixSeconds - remainder;
    }

    // Eorzea hour of day (0-24) at the given real time
    public static double EorzeaHourOfDay(long unixSeconds)
    {
        long remainder = unixSeconds % SecondsPerEorzeaDay;
        if (remainder < 0) remainder += SecondsPerEorzeaDay;
        return remainder / (double)SecondsPerEorzeaHour;
    }

    public static long UtcNowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
