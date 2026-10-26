using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectionTracker.Core.Report;

public sealed record ReportWarning(string Level, string Title, string Detail);

/// <summary>Outcome of the most recent run of some kind, as shown on the report.</summary>
public sealed record RunSummary(DateTimeOffset At, bool Ok, string Message);

/// <summary>One platform's data and status.</summary>
/// <param name="Snapshots">This platform's snapshots, oldest first.</param>
/// <param name="Account">How to show the tracked account, e.g. "@me" or a Steam profile name.</param>
/// <param name="Connected">Credentials are stored, so the app fetches this platform itself.</param>
public sealed record PlatformReport(
    string Platform,
    IReadOnlyList<Snapshot> Snapshots,
    string? Account = null,
    string? AccountUrl = null,
    bool Connected = false,
    RunSummary? LastFetch = null,
    RunSummary? LastOkFetch = null);

/// <summary>Everything the report page needs.</summary>
/// <param name="View">"home" for the overview, or a platform id.</param>
/// <param name="Running">The job in progress (fetch, import, daily run), if any.</param>
/// <param name="FormToken">Request token the page sends with its Refresh request.</param>
public sealed record ReportContext(
    IReadOnlyList<PlatformReport> Platforms,
    string View,
    DateTimeOffset? NextRunAt,
    DateTimeOffset Now,
    string? Running = null,
    string? FormToken = null);

/// <summary>Renders the report page: the HTML template with all data embedded as JSON.</summary>
public static class ReportBuilder
{
    public const string HomeView = "home";
    private const string Placeholder = "__REPORT_DATA__";
    private const int MaxEvents = 5000;

    /// <summary>Fetched data older than this is flagged; exports are taken by hand, so they get longer.</summary>
    private static readonly TimeSpan StaleFetch = TimeSpan.FromDays(3);
    private static readonly TimeSpan StaleExport = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<string> TemplateHtml = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ConnectionTracker.Core.report.html")
                           ?? throw new InvalidOperationException("report.html resource missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Render(ReportContext context) => Render(BuildData(context));

    internal static string Render(object data)
    {
        // The default encoder escapes <, > and &, so the JSON can't close the <script> tag it sits in.
        var json = JsonSerializer.Serialize(data, JsonOptions);
        return TemplateHtml.Value.Replace(Placeholder, json, StringComparison.Ordinal);
    }

    internal static object BuildData(ReportContext context) => new
    {
        GeneratedAt = context.Now,
        context.View,
        context.NextRunAt,
        // Only offered when some platform has credentials to fetch with.
        Refresh = context.Platforms.Any(p => p.Connected) ? new { context.Running, Token = context.FormToken } : null,
        Platforms = context.Platforms.Select(p => PlatformData(p, context.Now)),
    };

    private static object PlatformData(PlatformReport report, DateTimeOffset now)
    {
        var info = Platforms.Get(report.Platform);
        var snapshots = report.Snapshots;
        var latest = snapshots.Count > 0 ? snapshots[^1] : null;
        var history = Diff.BuildHistory(snapshots);

        // Names come from fetches and Facebook exports (Instagram exports don't include them); keep the most recent one.
        var names = new Dictionary<string, string>();
        foreach (var snapshot in snapshots)
            foreach (var p in snapshot.Followers.Concat(snapshot.Following).Concat(snapshot.Friends))
                if (p.FullName is { Length: > 0 } name)
                    names[p.Username] = name;

        object lists;
        IEnumerable<string> listed;
        Func<string, object> person;
        if (info.Friends)
        {
            var friends = latest?.Friends.Select(p => p.Username).Order(StringComparer.Ordinal).ToList() ?? [];
            var friendsNow = friends.ToHashSet();
            lists = new { Friends = friends };
            listed = friends;
            person = u => new
            {
                Name = names.GetValueOrDefault(u),
                Friend = friendsNow.Contains(u),
                FriendSince = SinceDto(history.FriendSince.GetValueOrDefault(u)),
            };
        }
        else
        {
            var relationships = latest is null ? new Relationships([], [], []) : Diff.Relate(latest);
            var followsYouNow = latest?.Followers.Select(p => p.Username).ToHashSet() ?? [];
            var youFollowNow = latest?.Following.Select(p => p.Username).ToHashSet() ?? [];
            lists = relationships;
            listed = relationships.NotFollowingBack.Concat(relationships.Fans).Concat(relationships.Mutual);
            person = u => new
            {
                Name = names.GetValueOrDefault(u),
                FollowsYou = followsYouNow.Contains(u),
                YouFollow = youFollowNow.Contains(u),
                FollowsYouSince = SinceDto(history.FollowerSince.GetValueOrDefault(u)),
                YouFollowSince = SinceDto(history.FollowingSince.GetValueOrDefault(u)),
            };
        }

        var events = history.Events.AsEnumerable().Reverse().Take(MaxEvents).ToList();
        return new
        {
            info.Id,
            info.Name,
            info.Friends,
            info.CanFetch,
            report.Connected,
            report.Account,
            report.AccountUrl,
            SnapshotCount = snapshots.Count,
            FirstSnapshotAt = snapshots.Count > 0 ? snapshots[0].TakenAt : (DateTimeOffset?)null,
            PreviousAt = snapshots.Count > 1 ? snapshots[^2].TakenAt : (DateTimeOffset?)null,
            Latest = latest is null ? null : new
            {
                latest.TakenAt,
                latest.Source,
                latest.SourceRef,
                Followers = info.Friends ? (int?)null : latest.Followers.Count,
                Following = info.Friends ? (int?)null : latest.Following.Count,
                Friends = info.Friends ? latest.Friends.Count : (int?)null,
            },
            Warnings = Warnings(report, now),
            Lists = lists,
            People = events.Select(e => e.Username).Concat(listed).Distinct().ToDictionary(u => u, person),
            Events = events.Select(e => new
            {
                e.At,
                Kind = KindName(e.Kind),
                e.Username,
                e.After,
                e.PreviousUsername,
                FollowedSince = SinceDto(e.FollowedSince),
            }),
        };
    }

    private static object? SinceDto(Since? since) => since is null ? null : new { since.At, since.Exact, since.AtOrBefore };

    private static string KindName(EventKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    internal static List<ReportWarning> Warnings(PlatformReport report, DateTimeOffset now)
    {
        var warnings = new List<ReportWarning>();
        var latest = report.Snapshots.Count > 0 ? report.Snapshots[^1] : null;
        if (report.Connected && report.LastFetch is { Ok: false } failed)
        {
            var since = report.LastOkFetch is { } ok ? $"Last successful fetch: {ok.At:yyyy-MM-dd}." : "No fetch has succeeded yet.";
            warnings.Add(new("error", "The last fetch failed", $"{failed.Message} {since}"));
        }
        if (latest is null)
        {
            warnings.Add(report.Connected
                ? new("info", "No data yet", "The first snapshot is taken on the next run; press Refresh to take it right away.")
                : new("info", "Not set up", report.Platform switch
                {
                    Platforms.Instagram => "Install the browser extension (see Settings) for daily fetches, or upload an export there.",
                    Platforms.Steam => "Add your Steam profile and a Web API key on the Settings page for daily fetches.",
                    _ => "Upload a Facebook export on the Settings page. Facebook has no API for friends lists, so exports are the only source.",
                }));
        }
        else if (report.Connected && now - latest.TakenAt > StaleFetch)
        {
            warnings.Add(new("warn", $"This data is {(int)(now - latest.TakenAt).TotalDays} days old",
                "Daily fetches haven't produced a snapshot recently; the Settings page shows recent runs."));
        }
        else if (!report.Connected && now - latest.TakenAt > StaleExport)
        {
            warnings.Add(new("info", $"The latest export is {(int)(now - latest.TakenAt).TotalDays} days old",
                "Upload a newer export on the Settings page, or drop it into the watched folder, to see what changed since."));
        }
        return warnings;
    }
}
