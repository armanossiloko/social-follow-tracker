using Following.Core.Instagram;
using Following.Core.Report;
using Following.Core.Storage;

namespace Following.Core;

/// <summary>The operations the CLI and the scheduled background runner share.</summary>
public sealed class Jobs(AppPaths paths, Log log, TimeProvider? clock = null)
{
    /// <summary>The daily job skips fetching if a fetch succeeded more recently than this (e.g. a manual run).</summary>
    public static readonly TimeSpan MinFetchInterval = TimeSpan.FromHours(12);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SnapshotStore _store = new(paths.EnsureCreated());

    public AppPaths Paths => paths;

    /// <summary>Import exports from the watched folders, fetch if logged in, rebuild the report. Returns an exit code.</summary>
    public async Task<int> DailyAsync(CancellationToken ct = default)
    {
        log.Info($"Daily run started (data: {paths.Root})");
        var config = AppConfig.Load(paths);
        var ok = ScanExports(config);

        if (config.HasSession)
        {
            var lastOk = AppState.Load(paths).LastOkRun(RunKind.Fetch);
            if (lastOk is not null && _clock.GetUtcNow() - lastOk.At < MinFetchInterval)
                log.Info($"Skipping fetch: last successful fetch was at {lastOk.At.ToLocalTime():g}.");
            else
                ok &= await FetchAsync(config, ct);
        }
        else
        {
            log.Info("Not logged in; skipping fetch (exports only).");
        }

        WriteReport(config);
        log.Info("Daily run finished.");
        return ok ? 0 : 1;
    }

    public async Task<bool> FetchAsync(AppConfig config, CancellationToken ct = default)
    {
        var state = AppState.Load(paths);
        try
        {
            var sessionId = config.GetSessionId() ?? throw new InvalidOperationException("Not logged in. Run `following login` first.");
            var client = new InstagramClient(sessionId, config.CsrfToken, log: log);
            var snapshots = _store.LoadAll();
            var previous = snapshots.LastOrDefault(s => s.Source == SnapshotSource.Fetch) ?? snapshots.LastOrDefault();
            var snapshot = await SnapshotFetcher.FetchAsync(client, previous, log, ct);
            var file = _store.Save(snapshot);
            var message = $"{snapshot.Followers.Count} followers, {snapshot.Following.Count} following";
            state.AddRun(RunKind.Fetch, true, message);
            log.Info($"Fetched {message} -> {file}");
            return true;
        }
        catch (Exception e) when (e is InstagramException or System.Security.Cryptography.CryptographicException)
        {
            state.AddRun(RunKind.Fetch, false, e.Message);
            log.Error(e.Message);
            if (e is SessionExpiredException) log.Error("Log in again with `following login`.");
            return false;
        }
        finally
        {
            state.Save(paths);
        }
    }

    /// <summary>Import every not-yet-seen instagram-*.zip in the watched folders.</summary>
    public bool ScanExports(AppConfig config)
    {
        var ok = true;
        foreach (var dir in config.EffectiveExportDirs(paths))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var zip in Directory.EnumerateFiles(dir, "instagram-*.zip").Order())
            {
                var state = AppState.Load(paths);
                string sha;
                try
                {
                    sha = ExportReader.Sha256(zip);
                }
                catch (IOException e)
                {
                    log.Warn($"Can't read {zip} yet ({e.Message}); will retry next run.");
                    continue;
                }
                if (state.IsImported(sha)) continue;
                if (!ExportReader.ContainsFollowData(zip))
                {
                    log.Info($"Ignoring {Path.GetFileName(zip)}: no followers/following data in it.");
                    state.ImportedFiles.Add(new ImportedFile(sha, zip, _clock.GetUtcNow(), null));
                    state.Save(paths);
                    continue;
                }
                ok &= Import(zip, sha, state) is not null;
            }
        }
        return ok;
    }

    /// <summary>Import one export zip or extracted folder. Returns the snapshot, or null on failure.</summary>
    public Snapshot? Import(string path, bool force = false)
    {
        var state = AppState.Load(paths);
        var sha = File.Exists(path) ? ExportReader.Sha256(path) : null;
        if (sha is not null && state.IsImported(sha) && !force)
        {
            log.Warn($"{Path.GetFileName(path)} was already imported (use --force to import it again).");
            return null;
        }
        return Import(path, sha, state);
    }

    private Snapshot? Import(string path, string? sha, AppState state)
    {
        try
        {
            var snapshot = ExportReader.Load(path);
            var file = _store.Save(snapshot);
            if (sha is not null)
            {
                state.ImportedFiles.RemoveAll(f => f.Sha256 == sha);
                state.ImportedFiles.Add(new ImportedFile(sha, path, _clock.GetUtcNow(), file));
            }
            var message = $"{Path.GetFileName(path)}: {snapshot.Followers.Count} followers, {snapshot.Following.Count} following";
            state.AddRun(RunKind.Import, true, message);
            log.Info($"Imported {message} -> {file}");
            return snapshot;
        }
        catch (Exception e) when (e is ExportException or IOException or System.Text.Json.JsonException)
        {
            state.AddRun(RunKind.Import, false, $"{Path.GetFileName(path)}: {e.Message}");
            log.Error($"Import of {path} failed: {e.Message}");
            return null;
        }
        finally
        {
            state.Save(paths);
        }
    }

    public string WriteReport(AppConfig? config = null)
    {
        var report = ReportBuilder.Write(paths, _store.LoadAll(), config ?? AppConfig.Load(paths), AppState.Load(paths), _clock.GetUtcNow());
        log.Info($"Report written to {report}");
        return report;
    }
}
