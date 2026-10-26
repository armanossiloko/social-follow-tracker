using System.Globalization;
using ConnectionTracker.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ConnectionTracker.Web.Services;

/// <param name="UserId">Instagram account id the stored cookie belongs to.</param>
/// <param name="Source">Who stored it: "extension" or "manual".</param>
public sealed record SessionInfo(
    bool HasSession,
    string? Username,
    string? UserId,
    DateTimeOffset? UpdatedAt,
    string? Source,
    DateTimeOffset? ExtensionLastSeen);

/// <summary>
/// The Instagram session cookie and what we know about it, kept in the settings table. The cookie is encrypted
/// with ASP.NET Core Data Protection; its keys live on a separate volume, so a database dump alone can't reveal it.
/// </summary>
public sealed class SessionStore(TrackerDb db, IDataProtectionProvider protection)
{
    private const string UsernameKey = "instagram.username";
    private const string SessionKey = "instagram.session";
    private const string UserIdKey = "instagram.user_id";
    private const string UpdatedAtKey = "instagram.session_updated_at";
    private const string SourceKey = "instagram.session_source";
    private const string ExtensionSeenKey = "extension.last_seen";

    private IDataProtector Protector => protection.CreateProtector("Following.InstagramSession.v1");

    public async Task<string?> GetUsernameAsync(CancellationToken ct) => await Get(UsernameKey, ct);

    public Task<bool> HasSessionAsync(CancellationToken ct) => db.Settings.AnyAsync(s => s.Key == SessionKey, ct);

    public async Task<SessionInfo> GetInfoAsync(CancellationToken ct)
    {
        var all = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new SessionInfo(
            all.ContainsKey(SessionKey),
            all.GetValueOrDefault(UsernameKey),
            all.GetValueOrDefault(UserIdKey),
            Time(all.GetValueOrDefault(UpdatedAtKey)),
            all.GetValueOrDefault(SourceKey),
            Time(all.GetValueOrDefault(ExtensionSeenKey)));
    }

    /// <exception cref="System.Security.Cryptography.CryptographicException">The keys that encrypted it are gone.</exception>
    public async Task<string?> GetSessionIdAsync(CancellationToken ct) =>
        await Get(SessionKey, ct) is { } value ? Protector.Unprotect(value) : null;

    public async Task SaveAsync(string? username, string userId, string sessionId, string source, DateTimeOffset now, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(username)) await Set(UsernameKey, username.Trim().TrimStart('@'), ct);
        await Set(UserIdKey, userId, ct);
        await Set(SessionKey, Protector.Protect(sessionId), ct);
        await Set(UpdatedAtKey, now.ToString("O"), ct);
        await Set(SourceKey, source, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkExtensionSeenAsync(DateTimeOffset now, CancellationToken ct)
    {
        await Set(ExtensionSeenKey, now.ToString("O"), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearSessionAsync(CancellationToken ct)
    {
        await db.Settings.Where(s => s.Key == SessionKey || s.Key == UpdatedAtKey || s.Key == SourceKey).ExecuteDeleteAsync(ct);
    }

    private async Task<string?> Get(string key, CancellationToken ct) =>
        (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    private async Task Set(string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.FindAsync([key], ct);
        if (row is null) db.Settings.Add(new SettingRow { Key = key, Value = value });
        else row.Value = value;
    }

    private static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
}
