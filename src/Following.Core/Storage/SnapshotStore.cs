namespace Following.Core.Storage;

/// <summary>One JSON file per snapshot in the snapshots folder.</summary>
public sealed class SnapshotStore(AppPaths paths)
{
    public string Save(Snapshot snapshot)
    {
        Directory.CreateDirectory(paths.Snapshots);
        var stem = $"{snapshot.TakenAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{snapshot.Source}";
        var file = Path.Combine(paths.Snapshots, stem + ".json");
        for (var i = 2; File.Exists(file); i++)
            file = Path.Combine(paths.Snapshots, $"{stem}-{i}.json");
        Json.Write(file, snapshot);
        return Path.GetFileName(file);
    }

    /// <summary>All snapshots, oldest first.</summary>
    public List<Snapshot> LoadAll()
    {
        if (!Directory.Exists(paths.Snapshots)) return [];
        return Directory.EnumerateFiles(paths.Snapshots, "*.json")
            .Select(f => Json.Read<Snapshot>(f)!)
            .OrderBy(s => s.TakenAt)
            .ToList();
    }
}
