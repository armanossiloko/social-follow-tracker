using System.Security.Cryptography;
using System.Text.Json;
using Following.Core;
using Following.Core.Instagram;
using Following.Web.Data;
using Microsoft.Extensions.Options;

namespace Following.Web.Services;

public sealed record ImportResult(bool Ok, string Message);

/// <summary>Creates Instagram clients; a seam so tests can substitute the HTTP handler.</summary>
public class InstagramClientFactory(ILoggerFactory loggers)
{
    public virtual InstagramClient Create(string sessionId) =>
        new(sessionId, logger: loggers.CreateLogger<InstagramClient>());
}

/// <summary>Fetch, import and the daily run. Callers go through <see cref="JobRunner"/> so runs never overlap.</summary>
public sealed class FollowJobs(
    SnapshotRepository repo,
    SessionStore session,
    InstagramClientFactory clients,
    IOptions<FollowingOptions> options,
    TimeProvider clock,
    ILogger<FollowJobs> log)
{
    /// <summary>The daily run skips fetching if a fetch succeeded more recently than this (e.g. a manual one).</summary>
    public static readonly TimeSpan MinFetchInterval = TimeSpan.FromHours(12);

    public async Task<bool> DailyAsync(CancellationToken ct)
    {
        log.LogInformation("Daily run started");
        var ok = await ScanExportsAsync(ct);
        if (await session.HasSessionAsync(ct))
        {
            var lastOk = await repo.LastRunAsync(RunKind.Fetch, okOnly: true, ct);
            if (lastOk is not null && clock.GetUtcNow() - lastOk.At < MinFetchInterval)
                log.LogInformation("Skipping fetch: the last successful one was at {At}", lastOk.At);
            else
                ok &= await FetchAsync(ct);
        }
        else
        {
            log.LogInformation("No session cookie stored; skipping fetch (exports only)");
        }
        await repo.LogRunAsync(RunKind.Daily, ok, ok ? "Completed." : "Completed with errors; see the fetch/import entries.", clock.GetUtcNow(), ct);
        return ok;
    }

    public async Task<bool> FetchAsync(CancellationToken ct)
    {
        try
        {
            var sessionId = await session.GetSessionIdAsync(ct) ?? throw new InstagramException("No session cookie stored.");
            var snapshot = await SnapshotFetcher.FetchAsync(clients.Create(sessionId), await repo.LatestCountsAsync(ct), log, ct);
            await repo.SaveAsync(snapshot, ct);
            var message = $"{snapshot.Followers.Count} followers, {snapshot.Following.Count} following.";
            await repo.LogRunAsync(RunKind.Fetch, true, message, clock.GetUtcNow(), ct);
            log.LogInformation("Fetched {Message}", message);
            return true;
        }
        catch (Exception e) when (e is InstagramException or CryptographicException)
        {
            var message = e switch
            {
                CryptographicException => "The stored session cookie can't be decrypted (the keys volume was probably reset). The browser extension resends it within the hour.",
                SessionExpiredException => e.Message + " Log in to instagram.com again in your browser; the extension passes the new session on.",
                _ => e.Message,
            };
            await repo.LogRunAsync(RunKind.Fetch, false, message, clock.GetUtcNow(), ct);
            log.LogError("Fetch failed: {Message}", message);
            return false;
        }
    }

    /// <param name="zip">A seekable stream over the export zip.</param>
    public async Task<ImportResult> ImportAsync(Stream zip, string fileName, DateTimeOffset fallbackTakenAt, bool force, CancellationToken ct)
    {
        var sha = ExportReader.Sha256(zip);
        zip.Position = 0;
        if (!force && await repo.IsImportedAsync(sha, ct))
            return new(false, $"{fileName} was already imported.");
        try
        {
            var snapshot = ExportReader.LoadZip(zip, fileName, fallbackTakenAt);
            var id = await repo.SaveAsync(snapshot, ct);
            await repo.MarkImportedAsync(sha, fileName, id, clock.GetUtcNow(), ct);
            var message = $"{fileName}: {snapshot.Followers.Count} followers, {snapshot.Following.Count} following.";
            await repo.LogRunAsync(RunKind.Import, true, message, clock.GetUtcNow(), ct);
            log.LogInformation("Imported {Message}", message);
            return new(true, "Imported " + message);
        }
        catch (Exception e) when (e is ExportException or JsonException)
        {
            var message = $"{fileName}: {e.Message}";
            await repo.LogRunAsync(RunKind.Import, false, message, clock.GetUtcNow(), ct);
            log.LogWarning("Import failed: {Message}", message);
            return new(false, "Import failed. " + message);
        }
    }

    /// <summary>Import every not-yet-seen instagram-*.zip in the watched folder.</summary>
    public async Task<bool> ScanExportsAsync(CancellationToken ct)
    {
        var dir = options.Value.ExportsPath;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return true;
        var ok = true;
        foreach (var path in Directory.EnumerateFiles(dir, "instagram-*.zip").Order())
        {
            var name = Path.GetFileName(path);
            try
            {
                await using var stream = File.OpenRead(path);
                var sha = ExportReader.Sha256(stream);
                if (await repo.IsImportedAsync(sha, ct)) continue;
                if (!ExportReader.ContainsFollowData(path))
                {
                    log.LogInformation("Ignoring {File}: no followers/following data in it", name);
                    await repo.MarkImportedAsync(sha, name, null, clock.GetUtcNow(), ct);
                    continue;
                }
                stream.Position = 0;
                ok &= (await ImportAsync(stream, name, File.GetLastWriteTimeUtc(path), force: false, ct)).Ok;
            }
            catch (IOException e)
            {
                log.LogWarning("Can't read {File} yet ({Error}); will retry next run", name, e.Message);
            }
        }
        return ok;
    }
}
