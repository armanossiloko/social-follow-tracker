using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Following.Core.Report;

public sealed record ReportWarning(string Level, string Title, string Detail);

/// <summary>Outcome of the most recent run of some kind, as shown on the report.</summary>
public sealed record RunSummary(DateTimeOffset At, bool Ok, string Message);

/// <summary>Everything the report page needs besides the snapshots themselves.</summary>
/// <param name="Running">The job in progress (fetch, import, daily run), if any.</param>
/// <param name="FormToken">Request token the page sends with its Refresh request.</param>
public sealed record ReportContext(
    IReadOnlyList<Snapshot> Snapshots,
    string? Account,
    bool HasSession,
    RunSummary? LastFetch,
    RunSummary? LastOkFetch,
    DateTimeOffset? NextRunAt,
    DateTimeOffset Now,
    string? Running = null,
    string? FormToken = null);

/// <summary>Renders the report page: the HTML template with all data embedded as JSON.</summary>
public static class ReportBuilder
{
    private const string Placeholder = "__REPORT_DATA__";
    private const int MaxEvents = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<string> TemplateHtml = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Following.Core.report.html")
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

    internal static object BuildData(ReportContext context)
    {
        var snapshots = context.Snapshots;
        var latest = snapshots.Count > 0 ? snapshots[^1] : null;
        var history = Diff.BuildHistory(snapshots);
        var relationships = latest is null ? new Relationships([], [], []) : Diff.Relate(latest);
        var lastChanges = snapshots.Count > 1 ? Diff.Changes(snapshots[^2], latest!) : [];

        // Names come from fetches only (exports don't include them); keep the most recent known one.
        var names = new Dictionary<string, string>();
        foreach (var snapshot in snapshots)
            foreach (var person in snapshot.Followers.Concat(snapshot.Following))
                if (person.FullName is { Length: > 0 } name)
                    names[person.Username] = name;

        var usernames = history.Events.Select(e => e.Username)
            .Concat(relationships.NotFollowingBack).Concat(relationships.Fans).Concat(relationships.Mutual)
            .ToHashSet();
        var followsYouNow = latest?.Followers.Select(p => p.Username).ToHashSet() ?? [];
        var youFollowNow = latest?.Following.Select(p => p.Username).ToHashSet() ?? [];
        var people = usernames.ToDictionary(u => u, u => new
        {
            Name = names.GetValueOrDefault(u),
            FollowsYou = followsYouNow.Contains(u),
            YouFollow = youFollowNow.Contains(u),
            FollowsYouSince = SinceDto(history.FollowerSince.GetValueOrDefault(u)),
            YouFollowSince = SinceDto(history.FollowingSince.GetValueOrDefault(u)),
        });

        return new
        {
            GeneratedAt = context.Now,
            context.Account,
            context.NextRunAt,
            // Only offered when there's a session cookie to fetch with.
            Refresh = context.HasSession ? new { context.Running, Token = context.FormToken } : null,
            SnapshotCount = snapshots.Count,
            FirstSnapshotAt = snapshots.Count > 0 ? snapshots[0].TakenAt : (DateTimeOffset?)null,
            Latest = latest is null ? null : new
            {
                latest.TakenAt,
                latest.Source,
                latest.SourceRef,
                Followers = latest.Followers.Count,
                Following = latest.Following.Count,
                latest.FollowersExpected,
                latest.FollowingExpected,
            },
            PreviousAt = snapshots.Count > 1 ? snapshots[^2].TakenAt : (DateTimeOffset?)null,
            LastChanges = Count(lastChanges),
            Warnings = Warnings(latest, context),
            Lists = relationships,
            People = people,
            Events = history.Events.AsEnumerable().Reverse().Take(MaxEvents)
                .Select(e => new
                {
                    e.At,
                    Kind = KindName(e.Kind),
                    e.Username,
                    e.After,
                    e.PreviousUsername,
                    FollowedSince = SinceDto(e.FollowedSince),
                }),
            Trend = snapshots.Select(s => new { s.TakenAt, Followers = s.Followers.Count, Following = s.Following.Count }),
        };
    }

    private static object? SinceDto(Since? since) => since is null ? null : new { since.At, since.Exact, since.AtOrBefore };

    private static Dictionary<string, int> Count(List<FollowEvent> events) =>
        Enum.GetValues<EventKind>().ToDictionary(KindName, kind => events.Count(e => e.Kind == kind));

    private static string KindName(EventKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    internal static List<ReportWarning> Warnings(Snapshot? latest, ReportContext context)
    {
        var warnings = new List<ReportWarning>();
        if (context.HasSession && context.LastFetch is { Ok: false } failed)
        {
            var since = context.LastOkFetch is { } ok ? $"Last successful fetch: {ok.At:yyyy-MM-dd}." : "No fetch has succeeded yet.";
            warnings.Add(new("error", "The last fetch failed", $"{failed.Message} {since}"));
        }
        if (latest is null)
        {
            warnings.Add(new("info", "No data yet",
                context.HasSession
                    ? "The first snapshot is taken on the next run; use Fetch now on the Settings page to take it right away."
                    : "Install the browser extension (see Settings) for daily fetches, or upload an export there."));
        }
        else if (context.Now - latest.TakenAt > TimeSpan.FromDays(3))
        {
            warnings.Add(new("warn", $"This data is {(int)(context.Now - latest.TakenAt).TotalDays} days old",
                context.HasSession
                    ? "Daily fetches haven't produced a snapshot recently; the Settings page shows recent runs."
                    : "Upload a fresh Instagram export on the Settings page, or drop it into the watched folder."));
        }
        return warnings;
    }
}
