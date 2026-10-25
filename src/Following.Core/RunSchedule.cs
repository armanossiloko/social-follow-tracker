namespace Following.Core;

/// <summary>When the daily run is due. Pure functions so the scheduler's decisions are testable.</summary>
public static class RunSchedule
{
    /// <summary>The scheduled time on the local calendar day of <paramref name="instant"/>.</summary>
    public static DateTimeOffset DueOn(DateTimeOffset instant, TimeOnly at, TimeZoneInfo zone)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        var local = localDate.ToDateTime(at);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// The next time the daily run should start. If today's run was missed (the PC was off or asleep, or the
    /// container wasn't running), it is due right away; otherwise at the next scheduled time.
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, DateTimeOffset? lastRun, TimeOnly at, TimeZoneInfo zone)
    {
        var today = DueOn(now, at, zone);
        if (now >= today)
            return lastRun is null || lastRun < today ? now : DueOn(now.AddDays(1), at, zone);
        return today;
    }
}
