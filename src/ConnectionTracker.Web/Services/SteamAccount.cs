using ConnectionTracker.Core;
using ConnectionTracker.Core.Steam;
using ConnectionTracker.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ConnectionTracker.Web.Services;

public sealed record SteamInfo(bool Connected, string? SteamId, string? Name, DateTimeOffset? UpdatedAt)
{
    public string? ProfileUrl => SteamId is null ? null : $"https://steamcommunity.com/profiles/{SteamId}";
}

/// <summary>Creates Steam clients; a seam so tests can substitute the HTTP handler.</summary>
public class SteamClientFactory(ILoggerFactory loggers)
{
    public virtual SteamClient Create(string apiKey) => new(apiKey, logger: loggers.CreateLogger<SteamClient>());
}

/// <summary>
/// The tracked Steam profile and the Web API key used to read it, kept in the settings table. The key is encrypted the
/// same way as the Instagram cookie.
/// </summary>
public sealed class SteamAccountStore(TrackerDb db, IDataProtectionProvider protection)
{
    private const string SteamIdKey = "steam.steam_id";
    private const string NameKey = "steam.name";
    private const string ApiKeyKey = "steam.api_key";
    private const string UpdatedAtKey = "steam.updated_at";

    private IDataProtector Protector => protection.CreateProtector("ConnectionTracker.SteamApiKey.v1");

    public Task<bool> IsConnectedAsync(CancellationToken ct) => db.Settings.AnyAsync(s => s.Key == ApiKeyKey, ct);

    public async Task<SteamInfo> GetInfoAsync(CancellationToken ct)
    {
        var all = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("steam.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new SteamInfo(
            all.ContainsKey(ApiKeyKey) && all.ContainsKey(SteamIdKey),
            all.GetValueOrDefault(SteamIdKey),
            all.GetValueOrDefault(NameKey),
            InstagramSessionStore.Time(all.GetValueOrDefault(UpdatedAtKey)));
    }

    /// <exception cref="System.Security.Cryptography.CryptographicException">The keys that encrypted it are gone.</exception>
    public async Task<(string SteamId, string ApiKey)?> GetCredentialsAsync(CancellationToken ct)
    {
        var steamId = await db.GetSettingAsync(SteamIdKey, ct);
        var apiKey = await db.GetSettingAsync(ApiKeyKey, ct);
        return steamId is null || apiKey is null ? null : (steamId, Protector.Unprotect(apiKey));
    }

    public async Task SaveAsync(string steamId, string? name, string apiKey, DateTimeOffset now, CancellationToken ct)
    {
        await db.SetSettingAsync(SteamIdKey, steamId, ct);
        if (!string.IsNullOrWhiteSpace(name)) await db.SetSettingAsync(NameKey, name, ct);
        await db.SetSettingAsync(ApiKeyKey, Protector.Protect(apiKey), ct);
        await db.SetSettingAsync(UpdatedAtKey, now.ToString("O"), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Forgets the key, so fetching stops. The profile id and name stay, to label the data already taken.</summary>
    public Task ClearAsync(CancellationToken ct) =>
        db.Settings.Where(s => s.Key == ApiKeyKey || s.Key == UpdatedAtKey).ExecuteDeleteAsync(ct);
}

/// <summary>Checks and stores the Steam profile and Web API key entered on the Settings page.</summary>
public sealed class SteamAccountUpdater(
    SteamAccountStore store,
    SnapshotRepository repo,
    SteamClientFactory clients,
    JobRunner runner,
    TimeProvider clock,
    ILogger<SteamAccountUpdater> log)
{
    public async Task<(bool Ok, string Message)> UpdateAsync(string? profile, string? apiKey, CancellationToken ct)
    {
        apiKey = apiKey?.Trim() ?? "";
        if (apiKey.Length == 0) return (false, "Enter your Steam Web API key.");
        var (steamId, vanity) = SteamClient.ParseProfile(profile ?? "");
        if (steamId is null && vanity is null)
            return (false, "Enter your SteamID64 (17 digits), your profile link, or your custom URL name.");

        var client = clients.Create(apiKey);
        string? name;
        try
        {
            steamId ??= await client.ResolveVanityAsync(vanity!, ct);
            if (steamId is null) return (false, $"Steam has no profile at steamcommunity.com/id/{vanity}.");
            // Reading the list once proves the key works and the list is visible, before anything is saved.
            await client.GetFriendsAsync(steamId, ct);
            name = (await client.GetPlayersAsync([steamId], ct)).GetValueOrDefault(steamId)?.Name;
        }
        catch (SteamException e)
        {
            return (false, e.Message);
        }

        await store.SaveAsync(steamId, name, apiKey, clock.GetUtcNow(), ct);
        log.LogInformation("Steam account saved for {Name} ({SteamId})", name, steamId);

        // Take the first snapshot right away instead of waiting for tomorrow.
        var lastFetch = await repo.LastRunAsync(RunKind.Fetch, Platforms.Steam, okOnly: false, ct);
        var fetching = (lastFetch is null || !lastFetch.Ok) && runner.TryStart("Steam fetch", (jobs, c) => jobs.FetchAsync(Platforms.Steam, c));
        return (true, $"Steam connected{(name is null ? "" : $" as {name}")}." + (fetching ? " Fetching now." : ""));
    }
}
