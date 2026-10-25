using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Following.Core;

public sealed class ExportException(string message) : Exception(message);

/// <summary>
/// Reads Instagram's official "Download your information" export (JSON format).
/// The relevant files live under connections/followers_and_following/:
///   followers_1.json, followers_2.json, ... and following.json.
/// Instagram has shipped two entry shapes; both are handled:
///   {"title": "", "string_list_data": [{"href": ".../alice", "value": "alice", "timestamp": 1700000000}]}
///   {"title": "alice", "string_list_data": [{"href": ".../_u/alice", "timestamp": 1700000000}]}
/// </summary>
public static partial class ExportReader
{
    [GeneratedRegex(@"^followers(_\d+)?\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex FollowersFile();

    /// <summary>Load from an export .zip or an extracted export folder on disk.</summary>
    public static Snapshot Load(string path)
    {
        if (Directory.Exists(path))
        {
            Dictionary<string, string> files = [];
            foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories))
                if (IsWanted(Path.GetRelativePath(path, file)))
                    files[Path.GetFileName(file).ToLowerInvariant()] = File.ReadAllText(file);
            return Parse(files, Directory.GetLastWriteTimeUtc(path), Path.GetFileName(path));
        }
        if (!File.Exists(path)) throw new ExportException($"{path} does not exist.");
        using var stream = File.OpenRead(path);
        return LoadZip(stream, Path.GetFileName(path), File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// Load from a zip stream (e.g. an upload). The snapshot is dated by the newest relevant file inside the zip,
    /// which is when Instagram generated the export; <paramref name="fallbackTakenAt"/> is used if that's missing.
    /// </summary>
    public static Snapshot LoadZip(Stream zipStream, string name, DateTimeOffset fallbackTakenAt)
    {
        Dictionary<string, string> files = [];
        DateTimeOffset? newest = null;
        try
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                if (!IsWanted(entry.FullName)) continue;
                using var reader = new StreamReader(entry.Open());
                files[entry.Name.ToLowerInvariant()] = reader.ReadToEnd();
                if (newest is null || entry.LastWriteTime > newest) newest = entry.LastWriteTime;
            }
        }
        catch (InvalidDataException)
        {
            throw new ExportException($"{name} is not a valid zip file.");
        }
        // Zip timestamps are optional (DOS epoch 1980 when absent) and carry no time zone; ignore implausible ones.
        var takenAt = newest is { Year: >= 2010 } t && t <= DateTimeOffset.UtcNow.AddDays(1) ? t : fallbackTakenAt;
        return Parse(files, takenAt.ToUniversalTime(), name);
    }

    /// <summary>Quick check used when scanning Downloads: does this zip hold follower data?</summary>
    public static bool ContainsFollowData(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e => IsWanted(e.FullName));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256(stream);
    }

    public static string Sha256(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

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
            ParseEntries(EntryArray(files[name]), followers);
        var following = new Dictionary<string, Person>();
        ParseEntries(EntryArray(files["following.json"]), following);

        return new Snapshot
        {
            TakenAt = takenAt,
            Source = SnapshotSource.Export,
            SourceRef = sourceRef,
            Followers = [.. followers.Values],
            Following = [.. following.Values],
        };
    }

    private static bool IsWanted(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        var name = parts[^1];
        return parts.Contains("followers_and_following", StringComparer.OrdinalIgnoreCase)
               && (FollowersFile().IsMatch(name) || name.Equals("following.json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A file is either a bare array or an object wrapping one (e.g. "relationships_following").</summary>
    private static JsonElement EntryArray(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var prop in root.EnumerateObject())
                if (prop.Name.StartsWith("relationships_", StringComparison.Ordinal) && prop.Value.ValueKind == JsonValueKind.Array)
                    return prop.Value;
        throw new ExportException("Unexpected structure in followers/following JSON.");
    }

    private static void ParseEntries(JsonElement entries, Dictionary<string, Person> into)
    {
        foreach (var entry in entries.EnumerateArray())
        {
            JsonElement data = default;
            if (entry.TryGetProperty("string_list_data", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                data = list[0];

            var username = StringProp(data, "value") ?? StringProp(entry, "title") ?? UsernameFromHref(StringProp(data, "href"));
            if (username is null) continue;

            DateTimeOffset? followedAt = null;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var seconds) && seconds > 0)
                followedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);

            var key = Usernames.Normalize(username);
            into[key] = new Person(key, FollowedAt: followedAt);
        }
    }

    private static string? StringProp(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    private static string? UsernameFromHref(string? href)
    {
        if (href is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        var last = uri.AbsolutePath.Trim('/').Split('/')[^1];
        return last is { Length: > 0 } and not "_u" ? last : null;
    }
}
