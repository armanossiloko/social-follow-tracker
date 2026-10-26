using System.Security.Cryptography;
using ConnectionTracker.Core;
using ConnectionTracker.Core.Instagram;
using ConnectionTracker.Web.Data;

namespace ConnectionTracker.Web.Services;

public static class SessionSource
{
    public const string Extension = "extension";
    public const string Manual = "manual";
}

/// <summary>Body the browser extension posts to /api/session.</summary>
public sealed record SessionCookie(string? SessionId);

public sealed record SessionUpdate(bool Ok, string Message, bool Changed = false, bool WrongAccount = false);

/// <summary>Validates and stores a new session cookie, from the Settings form or the browser extension.</summary>
public sealed class InstagramSessionUpdater(
    InstagramSessionStore store,
    SnapshotRepository repo,
    InstagramClientFactory clients,
    JobRunner runner,
    TimeProvider clock,
    ILogger<InstagramSessionUpdater> log)
{
    /// <param name="sameAccountOnly">
    /// Refuse a cookie for a different Instagram account than the one being tracked. The extension sets this, so
    /// switching accounts in the browser doesn't mix two accounts' snapshots; pasting manually can change account.
    /// </param>
    public async Task<SessionUpdate> UpdateAsync(string? sessionId, string source, bool sameAccountOnly, CancellationToken ct)
    {
        sessionId = sessionId?.Trim() ?? "";
        string userId;
        try
        {
            userId = InstagramClient.UserIdFromSession(sessionId);
        }
        catch (ArgumentException e)
        {
            return new(false, e.Message);
        }

        string? stored = null;
        try
        {
            stored = await store.GetSessionIdAsync(ct);
        }
        catch (CryptographicException)
        {
            // Keys were reset; treat it as no cookie stored.
        }
        if (stored == sessionId) return new(true, "Cookie unchanged.");

        var info = await store.GetInfoAsync(ct);
        var trackedId = info.UserId ?? (stored is null ? null : TryUserId(stored));
        if (sameAccountOnly && trackedId is not null && trackedId != userId)
            return new(false,
                $"That cookie is for a different Instagram account than the one being tracked ({(info.Username is null ? $"id {trackedId}" : "@" + info.Username)}). " +
                "Switch back to that account in your browser, or paste a cookie on the Settings page to change account.",
                WrongAccount: true);

        // One request to Instagram: it checks the cookie and tells us the username.
        var client = clients.Create(sessionId);
        string? username;
        try
        {
            username = await client.GetUsernameAsync(ct);
            if (username is null) await client.CheckSessionAsync(ct);
        }
        catch (InstagramException e)
        {
            return new(false, "Instagram didn't accept the cookie: " + e.Message);
        }

        await store.SaveAsync(username ?? (trackedId == userId ? info.Username : null), userId, sessionId, source, clock.GetUtcNow(), ct);
        log.LogInformation("Session cookie updated by {Source} for {User}", source, username ?? userId);

        // A fresh cookie after a failed fetch (or before the first one): fetch now instead of waiting for tomorrow.
        var lastFetch = await repo.LastRunAsync(RunKind.Fetch, Platforms.Instagram, okOnly: false, ct);
        var fetching = (lastFetch is null || !lastFetch.Ok) && runner.TryStart("Instagram fetch", (jobs, c) => jobs.FetchAsync(Platforms.Instagram, c));

        return new(true, $"Cookie saved{(username is null ? "" : $" for @{username}")}." + (fetching ? " Fetching now." : ""), Changed: true);
    }

    private static string? TryUserId(string sessionId)
    {
        try
        {
            return InstagramClient.UserIdFromSession(sessionId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
