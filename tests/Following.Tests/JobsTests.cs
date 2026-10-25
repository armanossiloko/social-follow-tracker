using System.IO.Compression;
using Following.Core;
using Following.Core.Report;
using Following.Core.Storage;

namespace Following.Tests;

public sealed class JobsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("following-test-");
    private readonly AppPaths _paths;

    public JobsTests()
    {
        _paths = new AppPaths(Path.Combine(_dir.FullName, "data")).EnsureCreated();
        // Only watch the inbox, never the real Downloads folder.
        new AppConfig { Username = "me", ExportDirs = [_paths.Inbox] }.Save(_paths);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private void DropExport(string name, string[] followers, string[] following)
    {
        using var zip = ZipFile.Open(Path.Combine(_paths.Inbox, name), ZipArchiveMode.Create);
        ExportReaderTests.Add(zip, "connections/followers_and_following/followers_1.json",
            "[" + string.Join(",", followers.Select(u => $$"""{"title":"","string_list_data":[{"href":"https://www.instagram.com/{{u}}","value":"{{u}}","timestamp":1700000000}]}""")) + "]");
        ExportReaderTests.Add(zip, "connections/followers_and_following/following.json",
            """{"relationships_following":[""" + string.Join(",", following.Select(u => $$"""{"title":"{{u}}","string_list_data":[{"href":"https://www.instagram.com/_u/{{u}}","timestamp":1700000000}]}""")) + "]}");
    }

    [Fact]
    public async Task Daily_imports_new_exports_once_and_writes_the_report()
    {
        DropExport("instagram-me-1.zip", followers: ["ann", "ben"], following: ["ben", "cat"]);
        var jobs = new Jobs(_paths, Log.Null);

        Assert.Equal(0, await jobs.DailyAsync());
        Assert.Equal(0, await jobs.DailyAsync()); // same zip: not imported twice

        var snapshots = new SnapshotStore(_paths).LoadAll();
        Assert.Single(snapshots);
        var html = File.ReadAllText(_paths.Report);
        Assert.DoesNotContain("__REPORT_DATA__", html);
        Assert.Contains("\"notFollowingBack\":[\"cat\"]", html);
        Assert.Contains("\"fans\":[\"ann\"]", html);
    }

    [Fact]
    public async Task Second_export_produces_change_events()
    {
        var jobs = new Jobs(_paths, Log.Null);
        DropExport("instagram-me-1.zip", followers: ["ann", "ben"], following: ["ben"]);
        File.SetLastWriteTimeUtc(Path.Combine(_paths.Inbox, "instagram-me-1.zip"), DateTime.UtcNow.AddDays(-1));
        await jobs.DailyAsync();
        DropExport("instagram-me-2.zip", followers: ["ben", "dan"], following: ["ben"]);
        await jobs.DailyAsync();

        var html = File.ReadAllText(_paths.Report);
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
        var config = new AppConfig();
        config.SetSessionId("12345%3Aabc");
        var state = new AppState();
        state.AddRun(RunKind.Fetch, true, "ok");
        state.AddRun(RunKind.Fetch, false, "Instagram rejected the session (login_required).");

        var warnings = ReportBuilder.Warnings(DiffTests.Snap(1, [], []), config, state, new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));

        var w = Assert.Single(warnings);
        Assert.Equal("error", w.Level);
        Assert.Contains("login_required", w.Detail);
        Assert.Contains("Last successful fetch", w.Detail);
    }

    [Fact]
    public void Session_cookie_is_not_stored_in_plain_text()
    {
        var config = new AppConfig();
        config.SetSessionId("12345%3Asecret");
        config.Save(_paths);

        Assert.DoesNotContain("secret", File.ReadAllText(_paths.Config));
        Assert.Equal("12345%3Asecret", AppConfig.Load(_paths).GetSessionId());
    }
}
