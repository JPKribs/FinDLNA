namespace FinDLNA.Utilities;

// MARK: TimeConversionUtil
public static class TimeConversionUtil
{
    private const long TicksPerSecond = 10_000_000L;
    private const long TicksPerMillisecond = 10_000L;
    private const long TicksPerMinute = TicksPerSecond * 60L;
    private const long TicksPerHour = TicksPerMinute * 60L;

    // MARK: TicksToSeconds
    public static double TicksToSeconds(long ticks)
    {
        return (double)ticks / TicksPerSecond;
    }

    // MARK: SecondsToTicks
    public static long SecondsToTicks(double seconds)
    {
        return (long)(seconds * TicksPerSecond);
    }

    // MARK: TicksToMilliseconds
    public static long TicksToMilliseconds(long ticks)
    {
        return ticks / TicksPerMillisecond;
    }

    // MARK: MillisecondsToTicks
    public static long MillisecondsToTicks(long milliseconds)
    {
        return milliseconds * TicksPerMillisecond;
    }

    // MARK: TicksToMinutes
    public static double TicksToMinutes(long ticks)
    {
        return (double)ticks / TicksPerMinute;
    }

    // MARK: MinutesToTicks
    public static long MinutesToTicks(double minutes)
    {
        return (long)(minutes * TicksPerMinute);
    }

    // MARK: TicksToHours
    public static double TicksToHours(long ticks)
    {
        return (double)ticks / TicksPerHour;
    }

    // MARK: HoursToTicks
    public static long HoursToTicks(double hours)
    {
        return (long)(hours * TicksPerHour);
    }

    // MARK: TicksToTimeSpan
    public static TimeSpan TicksToTimeSpan(long ticks)
    {
        return new TimeSpan(ticks);
    }

    // MARK: TimeSpanToTicks
    public static long TimeSpanToTicks(TimeSpan timeSpan)
    {
        return timeSpan.Ticks;
    }

    // MARK: FormatTicksAsTime
    public static string FormatTicksAsTime(long ticks)
    {
        var timeSpan = new TimeSpan(ticks);
        
        if (timeSpan.TotalHours >= 1)
        {
            return timeSpan.ToString(@"h\:mm\:ss");
        }
        else
        {
            return timeSpan.ToString(@"m\:ss");
        }
    }

    // MARK: FormatTicksAsTimeWithMs
    public static string FormatTicksAsTimeWithMs(long ticks)
    {
        var timeSpan = new TimeSpan(ticks);
        
        if (timeSpan.TotalHours >= 1)
        {
            return timeSpan.ToString(@"h\:mm\:ss\.fff");
        }
        else
        {
            return timeSpan.ToString(@"m\:ss\.fff");
        }
    }

    // MARK: GetProgressPercentage
    public static double GetProgressPercentage(long currentTicks, long totalTicks)
    {
        if (totalTicks <= 0) return 0.0;
        return Math.Min(100.0, (double)currentTicks / totalTicks * 100.0);
    }

    // MARK: IsNearEnd
    public static bool IsNearEnd(long currentTicks, long totalTicks, double threshold = 0.9)
    {
        if (totalTicks <= 0) return false;
        return (double)currentTicks / totalTicks >= threshold;
    }

    // MARK: IsNearBeginning
    public static bool IsNearBeginning(long currentTicks, long minimumTicks)
    {
        return currentTicks <= minimumTicks;
    }

    // MARK: GetWatchedThreshold
    public static long GetWatchedThreshold(long totalTicks, double percentage = 0.8)
    {
        return (long)(totalTicks * percentage);
    }
}