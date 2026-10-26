using System.Text;
using System.Text.Json;

namespace ConnectionTracker.Core.Facebook;

/// <summary>
/// Facebook's part of a "Download your information" export: your_friends.json (friends.json in older exports) and
/// removed_friends.json, under connections/friends/ (friends_and_followers/ in older exports). Entries look like
///   {"friends_v2": [{"name": "Ann Example", "timestamp": 1700000000}]}
/// Facebook has no friends-list API, and the export lists names only (no ids or profile links), so people are
/// matched by name. Two friends with the same name are kept apart as "name", "name #2".
/// </summary>
public static class FacebookExport
{
    private const string FriendsFile = "your_friends.json";
    private const string OldFriendsFile = "friends.json";
    private const string RemovedFile = "removed_friends.json";

    public static bool IsWanted(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        var name = parts[^1].ToLowerInvariant();
        return name is FriendsFile or OldFriendsFile or RemovedFile
               && parts[..^1].Any(p => p.Contains("friends", StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="files">Lower-cased file name to JSON text.</param>
    internal static Snapshot Parse(Dictionary<string, string> files, DateTimeOffset takenAt, string sourceRef)
    {
        var friendsJson = files.GetValueOrDefault(FriendsFile) ?? files.GetValueOrDefault(OldFriendsFile)
            ?? throw new ExportException(
                "No your_friends.json found. Request the export in JSON format (not HTML) and include \"Friends\".");
        return new Snapshot
        {
            Platform = Platforms.Facebook,
            TakenAt = takenAt,
            Source = SnapshotSource.Export,
            SourceRef = sourceRef,
            Friends = People(ExportJson.EntryArray(friendsJson, "friends")),
            RemovedFriends = files.TryGetValue(RemovedFile, out var removed) ? People(ExportJson.EntryArray(removed, "removed friends")) : [],
        };
    }

    private static List<Person> People(JsonElement entries)
    {
        var people = new List<Person>();
        var seen = new Dictionary<string, int>();
        foreach (var entry in entries.EnumerateArray())
        {
            var data = ExportJson.ListData(entry);
            var name = ExportJson.StringProp(entry, "name") ?? ExportJson.StringProp(entry, "title") ?? ExportJson.StringProp(data, "value");
            if (name is null) continue;
            name = FixEncoding(name.Trim());
            var key = Key(name);
            var n = seen[key] = seen.GetValueOrDefault(key) + 1;
            people.Add(new Person(n == 1 ? key : $"{key} #{n}", name, FollowedAt: ExportJson.Timestamp(entry) ?? ExportJson.Timestamp(data)));
        }
        return people;
    }

    private static string Key(string name) => string.Join(' ', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Meta's exports write each UTF-8 byte of a string as its own \u00XX escape, so "José" arrives as "JosÃ©".
    /// Undo that when the result is valid UTF-8; leave anything else alone.
    /// </summary>
    internal static string FixEncoding(string text)
    {
        if (text.All(c => c < 0x80) || text.Any(c => c > 0xFF)) return text;
        var bytes = text.Select(c => (byte)c).ToArray();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }
}
