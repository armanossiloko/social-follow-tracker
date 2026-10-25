using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Following.Core.Storage;

public sealed class AppConfig
{
    private const string DpapiPrefix = "dpapi:";
    private static readonly byte[] Entropy = "following/sessionid"u8.ToArray();

    public string? Username { get; set; }

    /// <summary>The sessionid cookie, encrypted with Windows DPAPI (only your Windows user can decrypt it).</summary>
    public string? ProtectedSessionId { get; set; }

    public string? CsrfToken { get; set; }

    /// <summary>Folders scanned for official export zips (instagram-*.zip) on every daily run.</summary>
    public List<string>? ExportDirs { get; set; }

    [JsonIgnore]
    public bool HasSession => ProtectedSessionId is not null;

    public IReadOnlyList<string> EffectiveExportDirs(AppPaths paths) => ExportDirs ??
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        paths.Inbox,
    ];

    public string? GetSessionId()
    {
        if (ProtectedSessionId is null) return null;
        if (!ProtectedSessionId.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return ProtectedSessionId;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Session was encrypted with Windows DPAPI.");
        var bytes = ProtectedData.Unprotect(
            Convert.FromBase64String(ProtectedSessionId[DpapiPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    public void SetSessionId(string? sessionId)
    {
        if (sessionId is null || !OperatingSystem.IsWindows())
        {
            ProtectedSessionId = sessionId;
            return;
        }
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(sessionId), Entropy, DataProtectionScope.CurrentUser);
        ProtectedSessionId = DpapiPrefix + Convert.ToBase64String(bytes);
    }

    public static AppConfig Load(AppPaths paths) => Json.Read<AppConfig>(paths.Config) ?? new AppConfig();

    public void Save(AppPaths paths) => Json.Write(paths.Config, this);
}
