using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ConnectionTracker.Core.Facebook;
using ConnectionTracker.Core.Instagram;

namespace ConnectionTracker.Core;

public sealed class ExportException(string message) : Exception(message);

/// <summary>
/// Reads the official "Download your information" exports from Meta's Accounts Center (JSON format). The platform is
/// recognized by what's inside: Instagram's followers and following, or Facebook's friends. A zip holding both yields
/// a snapshot for each.
/// </summary>
public static class ExportReader
{
    private static readonly (string Platform, Func<string, bool> IsWanted, Func<Dictionary<string, string>, DateTimeOffset, string, Snapshot> Parse)[] Formats =
    [
        (Platforms.Instagram, InstagramExport.IsWanted, InstagramExport.Parse),
        (Platforms.Facebook, FacebookExport.IsWanted, FacebookExport.Parse),
    ];

    /// <summary>Load from an export .zip or an extracted export folder on disk.</summary>
    public static List<Snapshot> Load(string path)
    {
        if (Directory.Exists(path))
        {
            var files = Formats.ToDictionary(f => f.Platform, _ => new Dictionary<string, string>());
            foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories))
                foreach (var format in Formats.Where(f => f.IsWanted(Path.GetRelativePath(path, file))))
                    files[format.Platform][Path.GetFileName(file).ToLowerInvariant()] = File.ReadAllText(file);
            return Parse(files, Directory.GetLastWriteTimeUtc(path), Path.GetFileName(path));
        }
        if (!File.Exists(path)) throw new ExportException($"{path} does not exist.");
        using var stream = File.OpenRead(path);
        return LoadZip(stream, Path.GetFileName(path), File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// Load from a zip stream (e.g. an upload). Snapshots are dated by the newest relevant file inside the zip,
    /// which is when the export was generated; <paramref name="fallbackTakenAt"/> is used if that's missing.
    /// </summary>
    public static List<Snapshot> LoadZip(Stream zipStream, string name, DateTimeOffset fallbackTakenAt)
    {
        var files = Formats.ToDictionary(f => f.Platform, _ => new Dictionary<string, string>());
        DateTimeOffset? newest = null;
        try
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                var formats = Formats.Where(f => f.IsWanted(entry.FullName)).ToList();
                if (formats.Count == 0) continue;
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                foreach (var format in formats)
                    files[format.Platform][entry.Name.ToLowerInvariant()] = text;
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

    /// <summary>Quick check used when scanning the watched folder: does this zip hold connection data?</summary>
    public static bool ContainsConnectionData(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e => Formats.Any(f => f.IsWanted(e.FullName)));
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

    /// <param name="files">Per platform: lower-cased file name to JSON text.</param>
    private static List<Snapshot> Parse(Dictionary<string, Dictionary<string, string>> files, DateTimeOffset takenAt, string sourceRef)
    {
        var found = Formats.Where(f => files[f.Platform].Count > 0).ToList();
        if (found.Count == 0)
            throw new ExportException(
                "This doesn't contain Instagram followers and following or Facebook friends. Request the export in JSON " +
                "format (not HTML) and include \"Followers and following\" (Instagram) or \"Friends\" (Facebook).");
        return [.. found.Select(f => f.Parse(files[f.Platform], takenAt, sourceRef))];
    }
}

/// <summary>JSON shapes shared by Meta's exports.</summary>
internal static class ExportJson
{
    /// <summary>A file is either a bare array or an object wrapping one (e.g. "relationships_following", "friends_v2").</summary>
    public static JsonElement EntryArray(string json, string what)
    {
        var root = JsonDocument.Parse(json).RootElement;
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var prop in root.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    return prop.Value;
        throw new ExportException($"Unexpected structure in the {what} JSON.");
    }

    /// <summary>The first item of "string_list_data", where newer exports keep the value and timestamp.</summary>
    public static JsonElement ListData(JsonElement entry) =>
        entry.TryGetProperty("string_list_data", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? list[0]
            : default;

    public static string? StringProp(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    public static DateTimeOffset? Timestamp(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("timestamp", out var ts)
        && ts.TryGetInt64(out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
