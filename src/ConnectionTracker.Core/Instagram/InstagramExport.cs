using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConnectionTracker.Core.Instagram;

/// <summary>
/// Instagram's part of a "Download your information" export. The relevant files live under
/// connections/followers_and_following/: followers_1.json, followers_2.json, ... and following.json.
/// Instagram has shipped two entry shapes; both are handled:
///   {"title": "", "string_list_data": [{"href": ".../alice", "value": "alice", "timestamp": 1700000000}]}
///   {"title": "alice", "string_list_data": [{"href": ".../_u/alice", "timestamp": 1700000000}]}
/// </summary>
public static partial class InstagramExport
{
    [GeneratedRegex(@"^followers(_\d+)?\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex FollowersFile();

    public static bool IsWanted(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        var name = parts[^1];
        return parts.Contains("followers_and_following", StringComparer.OrdinalIgnoreCase)
               && (FollowersFile().IsMatch(name) || name.Equals("following.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="files">Lower-cased file name to JSON text.</param>
    internal static Snapshot Parse(Dictionary<string, string> files, DateTimeOffset takenAt, string sourceRef)
    {
        var followerFiles = files.Keys.Where(k => FollowersFile().IsMatch(k)).Order().ToList();
        if (followerFiles.Count == 0 || !files.ContainsKey("following.json"))
            throw new ExportException(
                "No followers_*.json / following.json found. Request the export in JSON format (not HTML) " +
                "and include \"Followers and following\".");

        var followers = new Dictionary<string, Person>();
        foreach (var name in followerFiles)
            ParseEntries(ExportJson.EntryArray(files[name], "followers"), followers);
        var following = new Dictionary<string, Person>();
        ParseEntries(ExportJson.EntryArray(files["following.json"], "following"), following);

        return new Snapshot
        {
            Platform = Platforms.Instagram,
            TakenAt = takenAt,
            Source = SnapshotSource.Export,
            SourceRef = sourceRef,
            Followers = [.. followers.Values],
            Following = [.. following.Values],
        };
    }

    private static void ParseEntries(JsonElement entries, Dictionary<string, Person> into)
    {
        foreach (var entry in entries.EnumerateArray())
        {
            var data = ExportJson.ListData(entry);
            var username = ExportJson.StringProp(data, "value") ?? ExportJson.StringProp(entry, "title")
                ?? UsernameFromHref(ExportJson.StringProp(data, "href"));
            if (username is null) continue;
            var key = Usernames.Normalize(username);
            into[key] = new Person(key, FollowedAt: ExportJson.Timestamp(data));
        }
    }

    private static string? UsernameFromHref(string? href)
    {
        if (href is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        var last = uri.AbsolutePath.Trim('/').Split('/')[^1];
        return last is { Length: > 0 } and not "_u" ? last : null;
    }
}
