using System.Net;
using System.Text;
using ConnectionTracker.Core;
using ConnectionTracker.Core.Steam;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectionTracker.Tests;

public class SteamClientTests
{
    private const string Me = "76561197960287930";

    /// <summary>Answers by API method name and records what was asked.</summary>
    private sealed class FakeSteam(Dictionary<string, (HttpStatusCode Status, string Body)> answers) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var method = request.RequestUri!.AbsolutePath.Split('/')[2];
            var (status, body) = answers[method];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string TwoFriends = """
        {"friendslist": {"friends": [
          {"steamid": "76561197960287931", "relationship": "friend", "friend_since": 1600000000},
          {"steamid": "76561197960287932", "relationship": "friend", "friend_since": 0}
        ]}}
        """;

    private const string Players = """
        {"response": {"players": [
          {"steamid": "76561197960287931", "personaname": "Ann", "profileurl": "https://steamcommunity.com/id/ann/"}
        ]}}
        """;

    [Theory]
    [InlineData("76561197960287930", Me, null)]
    [InlineData("https://steamcommunity.com/profiles/76561197960287930/", Me, null)]
    [InlineData("https://steamcommunity.com/id/gabe/", null, "gabe")]
    [InlineData("steamcommunity.com/id/gabe", null, "gabe")]
    [InlineData("gabe", null, "gabe")]
    [InlineData("", null, null)]
    public void Understands_ids_links_and_custom_urls(string input, string? steamId, string? vanity)
    {
        Assert.Equal((steamId, vanity), SteamClient.ParseProfile(input));
    }

    [Fact]
    public async Task Fetch_keys_friends_by_steam_id_and_names_them()
    {
        var steam = new FakeSteam(new()
        {
            ["GetFriendList"] = (HttpStatusCode.OK, TwoFriends),
            ["GetPlayerSummaries"] = (HttpStatusCode.OK, Players),
        });

        var snap = await SteamFetcher.FetchAsync(new SteamClient("KEY", steam), Me, null, NullLogger.Instance);

        Assert.Equal(Platforms.Steam, snap.Platform);
        Assert.Equal(
            [new Person("76561197960287931", "Ann", FollowedAt: DateTimeOffset.FromUnixTimeSeconds(1600000000)), new Person("76561197960287932")],
            snap.Friends);
        Assert.Contains($"steamid={Me}", steam.Requests[0].Query);
        Assert.Contains("key=KEY", steam.Requests[0].Query);
        Assert.Contains("steamids=76561197960287931,76561197960287932", steam.Requests[1].Query);
    }

    [Fact]
    public async Task A_private_friends_list_says_how_to_fix_it()
    {
        var steam = new FakeSteam(new() { ["GetFriendList"] = (HttpStatusCode.Unauthorized, "<html></html>") });
        var e = await Assert.ThrowsAsync<SteamException>(() => new SteamClient("KEY", steam).GetFriendsAsync(Me));
        Assert.Contains("Friends List to Public", e.Message);
    }

    [Fact]
    public async Task A_bad_key_is_reported_as_such()
    {
        var steam = new FakeSteam(new() { ["GetFriendList"] = (HttpStatusCode.Forbidden, "<html></html>") });
        await Assert.ThrowsAsync<SteamKeyRejectedException>(() => new SteamClient("KEY", steam).GetFriendsAsync(Me));
    }

    [Fact]
    public async Task An_empty_list_after_a_full_one_is_not_saved()
    {
        var steam = new FakeSteam(new() { ["GetFriendList"] = (HttpStatusCode.OK, "{}") });
        await Assert.ThrowsAsync<IncompleteFetchException>(() =>
            SteamFetcher.FetchAsync(new SteamClient("KEY", steam), Me, new ListCounts(0, 0, 40), NullLogger.Instance));
    }

    [Fact]
    public async Task Resolves_a_custom_url()
    {
        var steam = new FakeSteam(new() { ["ResolveVanityURL"] = (HttpStatusCode.OK, $$$"""{"response": {"steamid": "{{{Me}}}", "success": 1}}""") });
        Assert.Equal(Me, await new SteamClient("KEY", steam).ResolveVanityAsync("gabe"));

        var missing = new FakeSteam(new() { ["ResolveVanityURL"] = (HttpStatusCode.OK, """{"response": {"success": 42, "message": "No match"}}""") });
        Assert.Null(await new SteamClient("KEY", missing).ResolveVanityAsync("nobody"));
    }
}
