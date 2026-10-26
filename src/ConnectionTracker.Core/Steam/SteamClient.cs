using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectionTracker.Core.Steam;

public class SteamException(string message) : FetchException(message);

/// <summary>Steam refused the Web API key.</summary>
public sealed class SteamKeyRejectedException(string message) : SteamException(message);

public sealed record SteamPlayer(string SteamId, string? Name, string? ProfileUrl);

/// <summary>
/// Reads a friends list through Steam's official Web API (https://steamcommunity.com/dev). It needs a Web API key and
/// a friends list that's visible to the public: the API answers like a stranger looking at your profile.
/// </summary>
public sealed partial class SteamClient(string apiKey, HttpMessageHandler? handler = null, ILogger? logger = null)
{
    private const string BaseUrl = "https://api.steampowered.com";
    /// <summary>GetPlayerSummaries takes at most this many ids per request.</summary>
    private const int SummaryBatch = 100;

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly string _key = apiKey.Trim();
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    [GeneratedRegex(@"^\d{17}$")]
    private static partial Regex SteamId64();

    [GeneratedRegex(@"steamcommunity\.com/(profiles|id)/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileUrl();

    /// <summary>
    /// What the user typed to identify their profile: a SteamID64, a profile link, or a custom URL name.
    /// Returns the SteamID64, or the custom URL name still to be resolved with <see cref="ResolveVanityAsync"/>.
    /// </summary>
    public static (string? SteamId, string? Vanity) ParseProfile(string input)
    {
        input = input.Trim();
        if (ProfileUrl().Match(input) is { Success: true } m)
            return m.Groups[1].Value.Equals("profiles", StringComparison.OrdinalIgnoreCase) && SteamId64().IsMatch(m.Groups[2].Value)
                ? (m.Groups[2].Value, null)
                : (null, Uri.UnescapeDataString(m.Groups[2].Value));
        if (SteamId64().IsMatch(input)) return (input, null);
        return input.Length > 0 && !input.Contains('/') ? (null, input) : (null, null);
    }

    /// <summary>The SteamID64 behind a custom profile URL name, or null if there's no such profile.</summary>
    public async Task<string?> ResolveVanityAsync(string vanity, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"/ISteamUser/ResolveVanityURL/v1/?vanityurl={Uri.EscapeDataString(vanity)}", ct);
        var response = doc.RootElement.GetProperty("response");
        return response.TryGetProperty("success", out var s) && s.GetInt32() == 1 ? response.GetProperty("steamid").GetString() : null;
    }

    /// <summary>Everyone on the friends list, with when the friendship started.</summary>
    public async Task<List<(string SteamId, DateTimeOffset? Since)>> GetFriendsAsync(string steamId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"/ISteamUser/GetFriendList/v1/?steamid={steamId}&relationship=friend", ct);
        if (!doc.RootElement.TryGetProperty("friendslist", out var list) || !list.TryGetProperty("friends", out var friends))
            return []; // Steam leaves the list out entirely when you have no friends
        return [.. friends.EnumerateArray().Select(f => (
            f.GetProperty("steamid").GetString()!,
            f.TryGetProperty("friend_since", out var since) && since.TryGetInt64(out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : (DateTimeOffset?)null))];
    }

    /// <summary>Current profile names. Profiles Steam no longer knows are left out.</summary>
    public async Task<Dictionary<string, SteamPlayer>> GetPlayersAsync(IEnumerable<string> steamIds, CancellationToken ct = default)
    {
        var players = new Dictionary<string, SteamPlayer>();
        foreach (var batch in steamIds.Distinct().Chunk(SummaryBatch))
        {
            using var doc = await GetAsync($"/ISteamUser/GetPlayerSummaries/v2/?steamids={string.Join(',', batch)}", ct);
            foreach (var p in doc.RootElement.GetProperty("response").GetProperty("players").EnumerateArray())
            {
                var id = p.GetProperty("steamid").GetString()!;
                players[id] = new SteamPlayer(id,
                    p.TryGetProperty("personaname", out var name) ? name.GetString() : null,
                    p.TryGetProperty("profileurl", out var url) ? url.GetString() : null);
            }
        }
        return players;
    }

    private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var path = pathAndQuery.Split('?')[0];
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"{pathAndQuery}&key={Uri.EscapeDataString(_key)}", ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SteamException($"Network error talking to Steam: {e.Message}");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            _log.LogInformation("Steam {Path}: HTTP {Status}", path, status);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Forbidden:
                    throw new SteamKeyRejectedException("Steam rejected the Web API key (HTTP 403). Check it at steamcommunity.com/dev/apikey.");
                case HttpStatusCode.Unauthorized when path.Contains("GetFriendList"):
                    throw new SteamException(
                        "Steam won't show the friends list (HTTP 401): it's private. In Steam, open Edit Profile → Privacy Settings " +
                        "and set Friends List to Public.");
                case HttpStatusCode.TooManyRequests:
                    throw new SteamException("Steam is rate limiting requests (HTTP 429). Try again later.");
            }
            if (status >= 400)
                throw new SteamException($"Steam error: HTTP {status} from {path}.");
            try
            {
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (JsonException)
            {
                throw new SteamException($"Unexpected non-JSON response from Steam ({path}).");
            }
        }
    }
}
