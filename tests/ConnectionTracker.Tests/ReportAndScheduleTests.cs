using ConnectionTracker.Core;
using ConnectionTracker.Core.Report;

namespace ConnectionTracker.Tests;

public class ReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ReportContext Context(IReadOnlyList<Snapshot> instagram, IReadOnlyList<Snapshot>? steam = null, bool connected = false) =>
        new([
            new PlatformReport(Platforms.Instagram, instagram, "@me", Connected: connected),
            new PlatformReport(Platforms.Facebook, []),
            new PlatformReport(Platforms.Steam, steam ?? []),
        ], ReportBuilder.HomeView, NextRunAt: null, Now);

    [Fact]
    public void Embeds_every_platforms_data_and_diff()
    {
        var html = ReportBuilder.Render(Context(
            [
                DiffTests.Snap(1, followers: ["ann", "ben"], following: ["ben"]),
                DiffTests.Snap(2, followers: ["ben", "dan"], following: ["ben", "cat"]),
            ],
            steam: [DiffTests.Friends(1, ["7656"]), DiffTests.Friends(2, [])]));

        Assert.DoesNotContain("__REPORT_DATA__", html);
        Assert.Contains("\"view\":\"home\"", html);
        Assert.Contains("\"notFollowingBack\":[\"cat\"]", html);
        Assert.Contains("\"kind\":\"unfollowedYou\",\"username\":\"ann\"", html);
        Assert.Contains("\"kind\":\"followedYou\",\"username\":\"dan\"", html);
        Assert.Contains("\"kind\":\"friendRemoved\",\"username\":\"7656\"", html);
        Assert.Contains("\"id\":\"facebook\"", html);
    }

    [Fact]
    public void Refresh_is_offered_only_when_a_platform_is_connected()
    {
        var context = Context([DiffTests.Snap(1, [], [])]) with { FormToken = "tok", Running = "Fetch" };
        Assert.DoesNotContain("\"refresh\":", ReportBuilder.Render(context));

        var connected = Context([DiffTests.Snap(1, [], [])], connected: true) with { FormToken = "tok", Running = "Fetch" };
        Assert.Contains("\"refresh\":{\"running\":\"Fetch\",\"token\":\"tok\"}", ReportBuilder.Render(connected));
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
        var report = new PlatformReport(Platforms.Instagram, [DiffTests.Snap(1, [], [])], Connected: true,
            LastFetch: new RunSummary(Now, false, "Instagram rejected the session (login_required)."),
            LastOkFetch: new RunSummary(Now.AddDays(-2), true, "ok"));

        var w = Assert.Single(ReportBuilder.Warnings(report, Now));

        Assert.Equal("error", w.Level);
        Assert.Contains("login_required", w.Detail);
        Assert.Contains("Last successful fetch: 2026-09-30", w.Detail);
    }

    [Fact]
    public void Stale_data_is_flagged_sooner_when_fetched_than_when_exported()
    {
        var fetched = new PlatformReport(Platforms.Steam, [DiffTests.Friends(1, [])] /* Oct 1, Now is Oct 2 */, Connected: true);
        Assert.Empty(ReportBuilder.Warnings(fetched, Now));
        Assert.Equal("warn", Assert.Single(ReportBuilder.Warnings(fetched, Now.AddDays(5))).Level);

        var exported = new PlatformReport(Platforms.Facebook, [DiffTests.Friends(1, [], Platforms.Facebook)]);
        Assert.Empty(ReportBuilder.Warnings(exported, Now.AddDays(5)));
        Assert.Equal("info", Assert.Single(ReportBuilder.Warnings(exported, Now.AddDays(40))).Level);
    }

    [Fact]
    public void A_platform_without_data_or_credentials_says_how_to_set_it_up()
    {
        var w = Assert.Single(ReportBuilder.Warnings(new PlatformReport(Platforms.Steam, []), Now));
        Assert.Equal("Not set up", w.Title);
        Assert.Contains("Web API key", w.Detail);
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
