using ConnectionTracker.Core;
using Microsoft.EntityFrameworkCore;

namespace ConnectionTracker.Web.Data;

public sealed class TrackerDb(DbContextOptions<TrackerDb> options) : DbContext(options)
{
    public DbSet<SnapshotRow> Snapshots => Set<SnapshotRow>();
    public DbSet<EntryRow> Entries => Set<EntryRow>();
    public DbSet<ImportedFileRow> ImportedFiles => Set<ImportedFileRow>();
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SnapshotRow>(e =>
        {
            e.ToTable("snapshots");
            e.HasIndex(s => s.TakenAt);
            e.Property(s => s.Source).HasMaxLength(16);
            e.HasMany(s => s.Entries).WithOne().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<EntryRow>(e =>
        {
            e.ToTable("snapshot_entries");
            e.HasKey(x => new { x.SnapshotId, x.List, x.Username });
            e.Property(x => x.List).HasConversion<short>();
        });
        model.Entity<ImportedFileRow>(e =>
        {
            e.ToTable("imported_files");
            e.HasKey(f => f.Sha256);
        });
        model.Entity<RunRow>(e =>
        {
            e.ToTable("runs");
            e.HasIndex(r => new { r.Kind, r.At });
            e.Property(r => r.Kind).HasMaxLength(16);
        });
        model.Entity<SettingRow>(e =>
        {
            e.ToTable("settings");
            e.HasKey(s => s.Key);
        });
    }
}

public sealed class SnapshotRow
{
    public long Id { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public required string Source { get; set; }
    public string? SourceRef { get; set; }
    public int? FollowersExpected { get; set; }
    public int? FollowingExpected { get; set; }
    public List<EntryRow> Entries { get; set; } = [];
}

/// <summary>One person on one list in one snapshot.</summary>
public sealed class EntryRow
{
    public long SnapshotId { get; set; }
    public FollowList List { get; set; }
    public required string Username { get; set; }
    public string? FullName { get; set; }
    public string? Pk { get; set; }
    public DateTimeOffset? FollowedAt { get; set; }
}

public sealed class ImportedFileRow
{
    public required string Sha256 { get; set; }
    public required string FileName { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public long? SnapshotId { get; set; }
}

public sealed class RunRow
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Kind { get; set; }
    public bool Ok { get; set; }
    public required string Message { get; set; }
}

public static class RunKind
{
    /// <summary>The scheduled run as a whole; its timestamp decides whether today's run is still due.</summary>
    public const string Daily = "daily";
    public const string Fetch = "fetch";
    public const string Import = "import";
}

public sealed class SettingRow
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
