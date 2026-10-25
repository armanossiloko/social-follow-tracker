namespace Following.Core.Storage;

public sealed record ImportedFile(string Sha256, string Path, DateTimeOffset ImportedAt, string? SnapshotFile);

public sealed record RunRecord(DateTimeOffset At, string Kind, bool Ok, string Message);

public static class RunKind
{
    public const string Fetch = "fetch";
    public const string Import = "import";
}

/// <summary>Bookkeeping: which export files were already imported, and recent run outcomes.</summary>
public sealed class AppState
{
    private const int MaxRuns = 200;

    public List<ImportedFile> ImportedFiles { get; set; } = [];
    public List<RunRecord> Runs { get; set; } = [];

    public bool IsImported(string sha256) => ImportedFiles.Any(f => f.Sha256 == sha256);

    public void AddRun(string kind, bool ok, string message)
    {
        Runs.Add(new RunRecord(DateTimeOffset.UtcNow, kind, ok, message));
        if (Runs.Count > MaxRuns) Runs.RemoveRange(0, Runs.Count - MaxRuns);
    }

    public RunRecord? LastRun(string kind) => Runs.LastOrDefault(r => r.Kind == kind);

    public RunRecord? LastOkRun(string kind) => Runs.LastOrDefault(r => r.Kind == kind && r.Ok);

    public static AppState Load(AppPaths paths) => Json.Read<AppState>(paths.State) ?? new AppState();

    public void Save(AppPaths paths) => Json.Write(paths.State, this);
}
