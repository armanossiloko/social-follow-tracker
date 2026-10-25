using Following.Core;
using Following.Core.Report;

namespace Following.Tests;

public class ReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ReportContext Context(IReadOnlyList<Snapshot> snapshots, bool hasSession = false, RunSummary? lastFetch = null, RunSummary? lastOk = null) =>
        new(snapshots, "me", hasSession, lastFetch, lastOk, NextRunAt: null, Now);

    [Fact]
    public void Embeds_the_data_and_the_diff()
    {
        var html = ReportBuilder.Render(Context([
            DiffTests.Snap(1, followers: ["ann", "ben"], following: ["ben"]),
            DiffTests.Snap(2, followers: ["ben", "dan"], following: ["ben", "cat"]),
        ]));

        Assert.DoesNotContain("__REPORT_DATA__", html);
        Assert.Contains("\"notFollowingBack\":[\"cat\"]", html);
        Assert.Contains("\"kind\":\"unfollowedYou\",\"username\":\"ann\"", html);
        Assert.Contains("\"kind\":\"followedYou\",\"username\":\"dan\"", html);
    }

    [Fact]
    public void Embedded_data_cannot_break_out_of_the_script_tag()
    {
        var html = ReportBuilder.Render(new { Name = "</script><script>alert(1)</script>" });
        Assert.DoesNotContain("</script><script>alert(1)", html);
    }

    [Fact]
    public void Failed_fetch_shows_a_warning_with_the_last_good_date()
    {
        var context = Context([DiffTests.Snap(1, [], [])], hasSession: true,
            lastFetch: new RunSummary(Now, false, "Instagram rejected the session (login_required)."),
            lastOk: new RunSummary(Now.AddDays(-2), true, "ok"));

        var w = Assert.Single(ReportBuilder.Warnings(context.Snapshots[^1], context));

        Assert.Equal("error", w.Level);
        Assert.Contains("login_required", w.Detail);
        Assert.Contains("Last successful fetch: 2026-09-30", w.Detail);
    }

    [Fact]
    public void Stale_data_is_flagged()
    {
        var context = Context([DiffTests.Snap(1, [], [])] /* Oct 1, Now is Oct 2 */);
        Assert.Empty(ReportBuilder.Warnings(context.Snapshots[^1], context));

        var old = context with { Now = Now.AddDays(5) };
        Assert.Equal("warn", Assert.Single(ReportBuilder.Warnings(old.Snapshots[^1], old)).Level);
    }
}

public class RunScheduleTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+1", TimeSpan.FromHours(1), "UTC+1", "UTC+1");
    private static readonly TimeOnly NineAm = new(9, 0);

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Before_the_scheduled_time_waits_for_today()
    {
        Assert.Equal(Local(25, 9), RunSchedule.NextRun(Local(25, 7), lastRun: Local(24, 9, 12), NineAm, Zone));
    }

    [Fact]
    public void After_todays_run_waits_for_tomorrow()
    {
        Assert.Equal(Local(26, 9), RunSchedule.NextRun(Local(25, 14), lastRun: Local(25, 9, 20), NineAm, Zone));
    }

    [Fact]
    public void A_missed_run_is_due_immediately()
    {
        var now = Local(25, 14);
        Assert.Equal(now, RunSchedule.NextRun(now, lastRun: Local(24, 9, 5), NineAm, Zone)); // PC was off at 9:00
        Assert.Equal(now, RunSchedule.NextRun(now, lastRun: null, NineAm, Zone)); // first start
    }

    [Fact]
    public void Due_time_uses_the_local_calendar_day()
    {
        // 23:30 UTC on the 24th is already the 25th at UTC+1.
        var instant = new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(Local(25, 9), RunSchedule.DueOn(instant, NineAm, Zone));
    }
}
