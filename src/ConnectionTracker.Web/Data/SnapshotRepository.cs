using ConnectionTracker.Core;
using ConnectionTracker.Core.Report;
using Microsoft.EntityFrameworkCore;

namespace ConnectionTracker.Web.Data;

public sealed class SnapshotRepository(TrackerDb db)
{
    public async Task<long> SaveAsync(Snapshot snapshot, CancellationToken ct)
    {
        var row = new SnapshotRow
        {
            // timestamptz columns only accept UTC values.
            TakenAt = snapshot.TakenAt.ToUniversalTime(),
            Source = snapshot.Source,
            SourceRef = snapshot.SourceRef,
            FollowersExpected = snapshot.FollowersExpected,
            FollowingExpected = snapshot.FollowingExpected,
            Entries =
            [
                .. snapshot.Followers.Select(p => Entry(FollowList.Followers, p)),
                .. snapshot.Following.Select(p => Entry(FollowList.Following, p)),
            ],
        };
        db.Snapshots.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;

        static EntryRow Entry(FollowList list, Person p) => new()
        {
            List = list,
            Username = p.Username,
            FullName = p.FullName,
            Pk = p.Pk,
            FollowedAt = p.FollowedAt?.ToUniversalTime(),
        };
    }

    /// <summary>All snapshots, oldest first.</summary>
    public async Task<List<Snapshot>> LoadAllAsync(CancellationToken ct)
    {
        var rows = await db.Snapshots.AsNoTracking().OrderBy(s => s.TakenAt).ThenBy(s => s.Id).ToListAsync(ct);
        var entries = (await db.Entries.AsNoTracking().ToListAsync(ct)).ToLookup(e => e.SnapshotId);
        return rows.Select(r => new Snapshot
        {
            TakenAt = r.TakenAt,
            Source = r.Source,
            SourceRef = r.SourceRef,
            FollowersExpected = r.FollowersExpected,
            FollowingExpected = r.FollowingExpected,
            Followers = [.. entries[r.Id].Where(e => e.List == FollowList.Followers).Select(ToPerson)],
            Following = [.. entries[r.Id].Where(e => e.List == FollowList.Following).Select(ToPerson)],
        }).ToList();

        static Person ToPerson(EntryRow e) => new(e.Username, e.FullName, e.Pk, e.FollowedAt);
    }

    /// <summary>List sizes of the latest fetched snapshot (falling back to any), to sanity-check the next fetch.</summary>
    public async Task<(int Followers, int Following)?> LatestCountsAsync(CancellationToken ct)
    {
        var id = await db.Snapshots.OrderByDescending(s => s.Source == SnapshotSource.Fetch).ThenByDescending(s => s.TakenAt)
            .Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
        if (id is null) return null;
        var counts = await db.Entries.Where(e => e.SnapshotId == id)
            .GroupBy(e => e.List).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return (counts.FirstOrDefault(c => c.Key == FollowList.Followers)?.Count ?? 0,
                counts.FirstOrDefault(c => c.Key == FollowList.Following)?.Count ?? 0);
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

    public async Task LogRunAsync(string kind, bool ok, string message, DateTimeOffset now, CancellationToken ct)
    {
        db.Runs.Add(new RunRow { At = now.ToUniversalTime(), Kind = kind, Ok = ok, Message = message });
        await db.SaveChangesAsync(ct);
    }

    public Task<RunRow?> LastRunAsync(string kind, bool okOnly, CancellationToken ct) =>
        db.Runs.AsNoTracking().Where(r => r.Kind == kind && (!okOnly || r.Ok)).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);

    public Task<List<RunRow>> RecentRunsAsync(int count, CancellationToken ct) =>
        db.Runs.AsNoTracking().OrderByDescending(r => r.Id).Take(count).ToListAsync(ct);

    public static RunSummary? Summary(RunRow? run) => run is null ? null : new RunSummary(run.At, run.Ok, run.Message);
}
