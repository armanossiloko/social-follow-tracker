using System.Reflection;
using System.Text.Json;
using Following.Core.Storage;

namespace Following.Core.Report;

/// <summary>Renders the self-contained report.html: the page template with all data embedded as JSON.</summary>
public static class ReportBuilder
{
    private const string Placeholder = "__REPORT_DATA__";
    private const int MaxEvents = 5000;
    private static readonly JsonSerializerOptions CompactJson = new(Json.Options) { WriteIndented = false };

    public sealed record Warning(string Level, string Title, string Detail);

    public static string Write(AppPaths paths, IReadOnlyList<Snapshot> snapshots, AppConfig config, AppState state, DateTimeOffset now)
    {
        var html = Render(BuildData(snapshots, config, state, paths, now));
        var tmp = paths.Report + ".tmp";
        File.WriteAllText(tmp, html);
        File.Move(tmp, paths.Report, overwrite: true);
        return paths.Report;
    }

    internal static string Render(object data)
    {
        // The default encoder escapes <, > and &, so the JSON can't close the <script> tag it sits in.
        var json = JsonSerializer.Serialize(data, CompactJson);
        return Template().Replace(Placeholder, json, StringComparison.Ordinal);
    }

    internal static object BuildData(IReadOnlyList<Snapshot> snapshots, AppConfig config, AppState state, AppPaths paths, DateTimeOffset now)
    {
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
            GeneratedAt = now,
            Account = config.Username,
            DataDir = paths.Root,
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
            Warnings = Warnings(latest, config, state, now),
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

    internal static List<Warning> Warnings(Snapshot? latest, AppConfig config, AppState state, DateTimeOffset now)
    {
        var warnings = new List<Warning>();
        var lastFetch = state.LastRun(RunKind.Fetch);
        if (config.HasSession && lastFetch is { Ok: false })
        {
            var lastOk = state.LastOkRun(RunKind.Fetch);
            var since = lastOk is null ? "No fetch has succeeded yet." : $"Last successful fetch: {lastOk.At:yyyy-MM-dd}.";
            warnings.Add(new("error", "The daily fetch failed", $"{lastFetch.Message} {since}"));
        }
        if (latest is null)
        {
            warnings.Add(new("info", "No data yet",
                config.HasSession
                    ? "Run `following fetch` to take the first snapshot."
                    : "Run `following login` for automatic daily fetches, or `following import <export.zip>`."));
        }
        else if (now - latest.TakenAt > TimeSpan.FromDays(3))
        {
            warnings.Add(new("warn", $"This data is {(int)(now - latest.TakenAt).TotalDays} days old",
                config.HasSession
                    ? "Daily fetches haven't produced a snapshot recently; check following.log."
                    : "Request a fresh Instagram export; any instagram-*.zip in Downloads is picked up automatically."));
        }
        return warnings;
    }

    private static string Template()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Following.Core.report.html")
                           ?? throw new InvalidOperationException("report.html resource missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
