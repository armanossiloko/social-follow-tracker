using System.Security.Cryptography;
using System.Text.Json;
using ConnectionTracker.Core;
using ConnectionTracker.Core.Instagram;
using ConnectionTracker.Core.Steam;
using ConnectionTracker.Web.Data;
using Microsoft.Extensions.Options;

namespace ConnectionTracker.Web.Services;

public sealed record ImportResult(bool Ok, string Message);

/// <summary>Creates Instagram clients; a seam so tests can substitute the HTTP handler.</summary>
public class InstagramClientFactory(ILoggerFactory loggers)
{
    public virtual InstagramClient Create(string sessionId) =>
        new(sessionId, logger: loggers.CreateLogger<InstagramClient>());
}

/// <summary>Fetch, import and the daily run. Callers go through <see cref="JobRunner"/> so runs never overlap.</summary>
public sealed class TrackerJobs(
    SnapshotRepository repo,
    InstagramSessionStore instagram,
    SteamAccountStore steam,
    InstagramClientFactory instagramClients,
    SteamClientFactory steamClients,
    IOptions<TrackerOptions> options,
    TimeProvider clock,
    ILogger<TrackerJobs> log)
{
    /// <summary>The daily run skips fetching if a fetch succeeded more recently than this (e.g. a manual one).</summary>
    public static readonly TimeSpan MinFetchInterval = TimeSpan.FromHours(12);

    /// <summary>Zips in the watched folder that may be exports. Others are never opened.</summary>
    private static readonly string[] ExportPatterns = ["instagram-*.zip", "facebook-*.zip"];

    /// <summary>Platforms with credentials stored, i.e. the ones the app fetches itself.</summary>
    public async Task<List<string>> ConnectedAsync(CancellationToken ct)
    {
        var connected = new List<string>();
        if (await instagram.HasSessionAsync(ct)) connected.Add(Platforms.Instagram);
        if (await steam.IsConnectedAsync(ct)) connected.Add(Platforms.Steam);
        return connected;
    }

    public async Task<bool> DailyAsync(CancellationToken ct)
    {
        log.LogInformation("Daily run started");
        var ok = await ScanExportsAsync(ct);
        var connected = await ConnectedAsync(ct);
        if (connected.Count == 0) log.LogInformation("No credentials stored; skipping fetches (exports only)");
        foreach (var platform in connected)
        {
            var lastOk = await repo.LastRunAsync(RunKind.Fetch, platform, okOnly: true, ct);
            if (lastOk is not null && clock.GetUtcNow() - lastOk.At < MinFetchInterval)
                log.LogInformation("Skipping the {Platform} fetch: the last successful one was at {At}", platform, lastOk.At);
            else
                ok &= await FetchAsync(platform, ct);
        }
        await repo.LogRunAsync(RunKind.Daily, null, ok, ok ? "Completed." : "Completed with errors; see the fetch/import entries.", clock.GetUtcNow(), ct);
        return ok;
    }

    /// <summary>Fetches every connected platform, one after the other.</summary>
    public async Task<bool> FetchAllAsync(CancellationToken ct)
    {
        var ok = true;
        foreach (var platform in await ConnectedAsync(ct))
            ok &= await FetchAsync(platform, ct);
        return ok;
    }

    public async Task<bool> FetchAsync(string platform, CancellationToken ct)
    {
        try
        {
            var previous = await repo.LatestCountsAsync(platform, ct);
            var snapshot = platform switch
            {
                Platforms.Instagram => await FetchInstagramAsync(previous, ct),
                Platforms.Steam => await FetchSteamAsync(previous, ct),
                _ => throw new ArgumentException($"{platform} can't be fetched.", nameof(platform)),
            };
            await repo.SaveAsync(snapshot, ct);
            var message = Counts(snapshot) + ".";
            await repo.LogRunAsync(RunKind.Fetch, platform, true, message, clock.GetUtcNow(), ct);
            log.LogInformation("Fetched {Platform}: {Message}", platform, message);
            return true;
        }
        catch (Exception e) when (e is FetchException or CryptographicException)
        {
            var message = e switch
            {
                CryptographicException when platform == Platforms.Instagram =>
                    "The stored session cookie can't be decrypted (the keys volume was probably reset). The browser extension resends it within the hour.",
                CryptographicException => "The stored API key can't be decrypted (the keys volume was probably reset). Enter it again on the Settings page.",
                SessionExpiredException => e.Message + " Log in to instagram.com again in your browser; the extension passes the new session on.",
                SteamKeyRejectedException => e.Message + " Then enter it again on the Settings page.",
                _ => e.Message,
            };
            await repo.LogRunAsync(RunKind.Fetch, platform, false, message, clock.GetUtcNow(), ct);
            log.LogError("{Platform} fetch failed: {Message}", platform, message);
            return false;
        }
    }

    private async Task<Snapshot> FetchInstagramAsync(ListCounts? previous, CancellationToken ct)
    {
        var sessionId = await instagram.GetSessionIdAsync(ct) ?? throw new InstagramException("No session cookie stored.");
        return await InstagramFetcher.FetchAsync(instagramClients.Create(sessionId), previous, log, ct);
    }

    private async Task<Snapshot> FetchSteamAsync(ListCounts? previous, CancellationToken ct)
    {
        var (steamId, apiKey) = await steam.GetCredentialsAsync(ct) ?? throw new SteamException("No Steam account stored.");
        return await SteamFetcher.FetchAsync(steamClients.Create(apiKey), steamId, previous, log, ct);
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
            var snapshots = ExportReader.LoadZip(zip, fileName, fallbackTakenAt);
            long? firstId = null;
            foreach (var snapshot in snapshots)
            {
                var id = await repo.SaveAsync(snapshot, ct);
                firstId ??= id;
                await repo.LogRunAsync(RunKind.Import, snapshot.Platform, true, $"{fileName}: {Counts(snapshot)}.", clock.GetUtcNow(), ct);
            }
            await repo.MarkImportedAsync(sha, fileName, firstId, clock.GetUtcNow(), ct);
            var message = $"{fileName}: " + string.Join("; ", snapshots.Select(s => $"{Platforms.Get(s.Platform).Name}, {Counts(s)}")) + ".";
            log.LogInformation("Imported {Message}", message);
            return new(true, "Imported " + message);
        }
        catch (Exception e) when (e is ExportException or JsonException)
        {
            var message = $"{fileName}: {e.Message}";
            await repo.LogRunAsync(RunKind.Import, null, false, message, clock.GetUtcNow(), ct);
            log.LogWarning("Import failed: {Message}", message);
            return new(false, "Import failed. " + message);
        }
    }

    /// <summary>Import every not-yet-seen instagram-*.zip and facebook-*.zip in the watched folder.</summary>
    public async Task<bool> ScanExportsAsync(CancellationToken ct)
    {
        var dir = options.Value.ExportsPath;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return true;
        var ok = true;
        foreach (var path in ExportPatterns.SelectMany(p => Directory.EnumerateFiles(dir, p)).Order())
        {
            var name = Path.GetFileName(path);
            try
            {
                await using var stream = File.OpenRead(path);
                var sha = ExportReader.Sha256(stream);
                if (await repo.IsImportedAsync(sha, ct)) continue;
                if (!ExportReader.ContainsConnectionData(path))
                {
                    log.LogInformation("Ignoring {File}: no followers, following or friends in it", name);
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

    private static string Counts(Snapshot s) => Platforms.Get(s.Platform).Friends
        ? $"{s.Friends.Count} friends"
        : $"{s.Followers.Count} followers, {s.Following.Count} following";
}
