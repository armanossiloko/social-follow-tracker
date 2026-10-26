using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectionTracker.Core.Instagram;

public class InstagramException(string message) : FetchException(message);

public sealed class SessionExpiredException(string message) : InstagramException(message);

public sealed class RateLimitedException(string message) : InstagramException(message);

/// <summary>
/// Reads follower/following lists through the endpoints instagram.com itself calls.
/// Unofficial: it authenticates with your browser's sessionid cookie, which is against Instagram's
/// Terms of Use. It is deliberately slow (one page at a time with randomized pauses) and stops at
/// the first sign of trouble instead of retrying.
/// </summary>
public sealed class InstagramClient
{
    private const string BaseUrl = "https://www.instagram.com";
    private const string WebAppId = "936619743392459"; // public id instagram.com sends with its API calls
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    private const int PageSize = 50;

    private readonly HttpClient _http;
    private readonly string _sessionId;
    private readonly string? _csrfToken;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly (double Min, double Max) _pause;
    private readonly ILogger _log;
    private int _requests;

    public string UserId { get; }

    public InstagramClient(
        string sessionId,
        string? csrfToken = null,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, Task>? delay = null,
        (double Min, double Max)? pauseSeconds = null,
        ILogger? logger = null)
    {
        _sessionId = sessionId.Trim();
        _csrfToken = string.IsNullOrWhiteSpace(csrfToken) ? null : csrfToken.Trim();
        UserId = UserIdFromSession(_sessionId);
        // Cookies are sent by hand and redirects are not followed, so a bounce to the login page is detectable.
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _delay = delay ?? (t => Task.Delay(t));
        _pause = pauseSeconds ?? (2.5, 6.0);
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>The sessionid cookie starts with the numeric user id: "&lt;id&gt;%3A&lt;token&gt;%3A...".</summary>
    public static string UserIdFromSession(string sessionId)
    {
        var id = Uri.UnescapeDataString(sessionId.Trim()).Split(':')[0];
        if (id.Length == 0 || !id.All(char.IsAsciiDigit))
            throw new ArgumentException("That doesn't look like an Instagram sessionid cookie.");
        return id;
    }

    /// <summary>
    /// Username of the account the session belongs to, or null if Instagram doesn't say.
    /// Throws <see cref="SessionExpiredException"/> for a bad cookie, so it doubles as a session check.
    /// </summary>
    public async Task<string?> GetUsernameAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetAsync($"/api/v1/users/{UserId}/info/", ct);
            return doc.RootElement.GetProperty("user").GetProperty("username").GetString();
        }
        catch (InstagramException e) when (e is not SessionExpiredException and not RateLimitedException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Follower/following totals shown on the profile, used to sanity-check a fetch.</summary>
    public async Task<(int? Followers, int? Following)> GetProfileCountsAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetAsync($"/api/v1/users/{UserId}/info/", ct);
            var user = doc.RootElement.GetProperty("user");
            return (IntProp(user, "follower_count"), IntProp(user, "following_count"));
        }
        catch (InstagramException e) when (e is not SessionExpiredException and not RateLimitedException)
        {
            _log.LogWarning("Could not read profile counts ({Error}); skipping the completeness check.", e.Message);
            return (null, null);
        }
        catch (KeyNotFoundException)
        {
            return (null, null);
        }
    }

    public async Task<List<Person>> GetListAsync(ConnectionList list, CancellationToken ct = default)
    {
        var kind = list == ConnectionList.Followers ? "followers" : "following";
        var people = new Dictionary<string, Person>();
        string? maxId = null;
        do
        {
            var query = $"count={PageSize}";
            if (list == ConnectionList.Followers) query += "&search_surface=follow_list_page";
            if (maxId is not null) query += "&max_id=" + Uri.EscapeDataString(maxId);

            using var doc = await GetAsync($"/api/v1/friendships/{UserId}/{kind}/?{query}", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("users", out var users))
            {
                foreach (var u in users.EnumerateArray())
                {
                    var username = Usernames.Normalize(u.GetProperty("username").GetString()!);
                    var fullName = u.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
                    var pk = u.TryGetProperty("pk", out var pkEl) ? pkEl.ToString() : null;
                    people[username] = new Person(username, string.IsNullOrEmpty(fullName) ? null : fullName, pk);
                }
            }
            maxId = root.TryGetProperty("next_max_id", out var next) && next.ValueKind is not JsonValueKind.Null
                ? next.ToString()
                : null;
            if (maxId is { Length: 0 }) maxId = null;
            _log.LogInformation("{Kind}: {Count} so far", kind, people.Count);
        } while (maxId is not null);
        return [.. people.Values];
    }

    /// <summary>Cheap request that fails with <see cref="SessionExpiredException"/> if the cookie is bad.</summary>
    public async Task CheckSessionAsync(CancellationToken ct = default)
    {
        using var _ = await GetAsync($"/api/v1/friendships/{UserId}/following/?count=1", ct);
    }

    private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        if (_requests++ > 0)
            await _delay(TimeSpan.FromSeconds(_pause.Min + Random.Shared.NextDouble() * (_pause.Max - _pause.Min)));

        for (var attempt = 1; ; attempt++)
        {
            using var request = BuildRequest(pathAndQuery);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt == 2) throw new InstagramException($"Network error: {e.Message}");
                _log.LogWarning("Network error ({Error}); retrying in 30s.", e.Message);
                await _delay(TimeSpan.FromSeconds(30));
                continue;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location?.ToString() ?? "";
                    if (location.Contains("/accounts/login", StringComparison.OrdinalIgnoreCase) ||
                        location.Contains("/challenge", StringComparison.OrdinalIgnoreCase))
                        throw new SessionExpiredException("Instagram redirected to the login page; the session cookie is no longer valid.");
                    throw new InstagramException($"Unexpected redirect (HTTP {status}) to {location}");
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(body);
                }
                catch (JsonException)
                {
                    throw new InstagramException($"Unexpected non-JSON response (HTTP {status}) from {pathAndQuery.Split('?')[0]}");
                }

                var root = doc.RootElement;
                var message = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) ? m.ToString() : "";
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                    message is "login_required" or "checkpoint_required" or "challenge_required")
                {
                    doc.Dispose();
                    var hint = message.Contains("checkpoint") || message.Contains("challenge")
                        ? " Open instagram.com in your browser and confirm it's you, then log in here again."
                        : "";
                    throw new SessionExpiredException(
                        $"Instagram rejected the session ({(message.Length > 0 ? message : $"HTTP {status}")}).{hint}");
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("spam", out var spam) && spam.ValueKind == JsonValueKind.True) ||
                    message.Contains("wait a few minutes", StringComparison.OrdinalIgnoreCase))
                {
                    doc.Dispose();
                    throw new RateLimitedException($"Instagram is rate limiting requests ({(message.Length > 0 ? message : $"HTTP {status}")}). Try again tomorrow.");
                }
                var apiStatus = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var s) ? s.GetString() : "ok";
                if (status >= 400 || apiStatus != "ok")
                {
                    doc.Dispose();
                    throw new InstagramException($"Instagram error: HTTP {status} {message}".Trim());
                }
                return doc;
            }
        }
    }

    private HttpRequestMessage BuildRequest(string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        var cookie = $"sessionid={_sessionId}; ds_user_id={UserId}";
        if (_csrfToken is not null)
        {
            cookie += $"; csrftoken={_csrfToken}";
            request.Headers.TryAddWithoutValidation("X-CSRFToken", _csrfToken);
        }
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("X-IG-App-ID", WebAppId);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        return request;
    }

    private static int? IntProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
}
