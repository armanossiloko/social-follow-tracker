using Following.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Following.Web.Services;

/// <summary>
/// The Instagram username and session cookie, kept in the settings table. The cookie is encrypted with
/// ASP.NET Core Data Protection; its keys live on a separate volume, so a database dump alone can't reveal it.
/// </summary>
public sealed class SessionStore(FollowingDb db, IDataProtectionProvider protection)
{
    private const string UsernameKey = "instagram.username";
    private const string SessionKey = "instagram.session";

    private IDataProtector Protector => protection.CreateProtector("Following.InstagramSession.v1");

    public async Task<string?> GetUsernameAsync(CancellationToken ct) =>
        (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == UsernameKey, ct))?.Value;

    public Task<bool> HasSessionAsync(CancellationToken ct) => db.Settings.AnyAsync(s => s.Key == SessionKey, ct);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The keys that encrypted it are gone.</exception>
    public async Task<string?> GetSessionIdAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SessionKey, ct);
        return row is null ? null : Protector.Unprotect(row.Value);
    }

    public async Task SaveAsync(string? username, string sessionId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(username)) await Set(UsernameKey, username.Trim().TrimStart('@'), ct);
        await Set(SessionKey, Protector.Protect(sessionId.Trim()), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearSessionAsync(CancellationToken ct)
    {
        await db.Settings.Where(s => s.Key == SessionKey).ExecuteDeleteAsync(ct);
    }

    private async Task Set(string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.FindAsync([key], ct);
        if (row is null) db.Settings.Add(new SettingRow { Key = key, Value = value });
        else row.Value = value;
    }
}
