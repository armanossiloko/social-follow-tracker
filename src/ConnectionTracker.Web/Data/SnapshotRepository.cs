using ConnectionTracker.Core;
using ConnectionTracker.Core.Report;
using Microsoft.EntityFrameworkCore;

namespace ConnectionTracker.Web.Data;

public sealed class SnapshotRepository(TrackerDb db)
{
    private static readonly ConnectionList[] Lists = Enum.GetValues<ConnectionList>();

    public async Task<long> SaveAsync(Snapshot snapshot, CancellationToken ct)
    {
        var row = new SnapshotRow
        {
            Platform = snapshot.Platform,
            // timestamptz columns only accept UTC values.
            TakenAt = snapshot.TakenAt.ToUniversalTime(),
            Source = snapshot.Source,
            SourceRef = snapshot.SourceRef,
            FollowersExpected = snapshot.FollowersExpected,
            FollowingExpected = snapshot.FollowingExpected,
            Entries = [.. Lists.SelectMany(list => snapshot.List(list).Select(p => Entry(list, p)))],
        };
        db.Snapshots.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;

        static EntryRow Entry(ConnectionList list, Person p) => new()
        {
            List = list,
            Username = p.Username,
            FullName = p.FullName,
            Pk = p.Pk,
            FollowedAt = p.FollowedAt?.ToUniversalTime(),
        };
    }

    /// <summary>All snapshots of each platform, oldest first. Every platform has an entry, empty if it has no data.</summary>
    public async Task<Dictionary<string, List<Snapshot>>> LoadAllAsync(CancellationToken ct)
    {
        var rows = await db.Snapshots.AsNoTracking().OrderBy(s => s.TakenAt).ThenBy(s => s.Id).ToListAsync(ct);
        var entries = (await db.Entries.AsNoTracking().ToListAsync(ct)).ToLookup(e => e.SnapshotId);
        var all = Platforms.All.ToDictionary(p => p.Id, _ => new List<Snapshot>());
        foreach (var r in rows)
        {
            var people = entries[r.Id].ToLookup(e => e.List, e => new Person(e.Username, e.FullName, e.Pk, e.FollowedAt));
            if (!all.TryGetValue(r.Platform, out var list)) continue; // a platform this version doesn't know
            list.Add(new Snapshot
            {
                Platform = r.Platform,
                TakenAt = r.TakenAt,
                Source = r.Source,
                SourceRef = r.SourceRef,
                FollowersExpected = r.FollowersExpected,
                FollowingExpected = r.FollowingExpected,
                Followers = [.. people[ConnectionList.Followers]],
                Following = [.. people[ConnectionList.Following]],
                Friends = [.. people[ConnectionList.Friends]],
                RemovedFriends = [.. people[ConnectionList.RemovedFriends]],
            });
        }
        return all;
    }

    /// <summary>List sizes of the platform's latest fetched snapshot (falling back to any), to sanity-check the next fetch.</summary>
    public async Task<ListCounts?> LatestCountsAsync(string platform, CancellationToken ct)
    {
        var id = await db.Snapshots.Where(s => s.Platform == platform)
            .OrderByDescending(s => s.Source == SnapshotSource.Fetch).ThenByDescending(s => s.TakenAt)
            .Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
        if (id is null) return null;
        var counts = await db.Entries.Where(e => e.SnapshotId == id)
            .GroupBy(e => e.List).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(c => c.Key, c => c.Count, ct);
        return new ListCounts(
            counts.GetValueOrDefault(ConnectionList.Followers),
            counts.GetValueOrDefault(ConnectionList.Following),
            counts.GetValueOrDefault(ConnectionList.Friends));
    }

    public Task<bool> IsImportedAsync(string sha256, CancellationToken ct) =>
        db.ImportedFiles.AnyAsync(f => f.Sha256 == sha256, ct);

    public async Task MarkImportedAsync(string sha256, string fileName, long? snapshotId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.ImportedFiles.FindAsync([sha256], ct);
        if (row is null)
            db.ImportedFiles.Add(new ImportedFileRow { Sha256 = sha256, FileName = fileName, ImportedAt = now, SnapshotId = snapshotId });
        else
            (row.FileName, row.ImportedAt, row.SnapshotId) = (fileName, now, snapshotId);
        await db.SaveChangesAsync(ct);
    }

    // -- run log -------------------------------------------------------------

    /// <param name="platform">The platform a fetch or import was for; null for the daily run.</param>
    public async Task LogRunAsync(string kind, string? platform, bool ok, string message, DateTimeOffset now, CancellationToken ct)
    {
        db.Runs.Add(new RunRow { At = now.ToUniversalTime(), Kind = kind, Platform = platform, Ok = ok, Message = message });
        await db.SaveChangesAsync(ct);
    }

    public Task<RunRow?> LastRunAsync(string kind, string? platform, bool okOnly, CancellationToken ct) =>
        db.Runs.AsNoTracking().Where(r => r.Kind == kind && r.Platform == platform && (!okOnly || r.Ok))
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<List<RunRow>> RecentRunsAsync(int count, CancellationToken ct) =>
        db.Runs.AsNoTracking().OrderByDescending(r => r.Id).Take(count).ToListAsync(ct);

    public static RunSummary? Summary(RunRow? run) => run is null ? null : new RunSummary(run.At, run.Ok, run.Message);
}
