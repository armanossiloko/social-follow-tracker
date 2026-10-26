using System.Net;
using System.Text;
using ConnectionTracker.Core;
using ConnectionTracker.Core.Instagram;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConnectionTracker.Tests;

public class InstagramClientTests
{
    private const string Session = "12345%3AabcDEF%3A9%3AAYx";

    /// <summary>Answers requests from a queue of canned responses and records what was asked.</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static InstagramClient Client(FakeHandler handler, List<TimeSpan>? delays = null) =>
        new(Session, handler: handler, delay: t => { delays?.Add(t); return Task.CompletedTask; });

    [Fact]
    public void Reads_user_id_from_the_session_cookie()
    {
        Assert.Equal("12345", InstagramClient.UserIdFromSession(Session));
        Assert.Equal("12345", InstagramClient.UserIdFromSession("12345:abc:9"));
        Assert.Throws<ArgumentException>(() => InstagramClient.UserIdFromSession("not-a-cookie"));
    }

    [Fact]
    public async Task Paginates_and_pauses_between_requests()
    {
        var handler = new FakeHandler(
            Json("""{"users": [{"pk": 1, "username": "Ann", "full_name": "Ann A"}], "next_max_id": "50", "status": "ok"}"""),
            Json("""{"users": [{"pk": 2, "username": "ben", "full_name": ""}], "status": "ok"}"""));
        var delays = new List<TimeSpan>();

        var people = await Client(handler, delays).GetListAsync(FollowList.Followers);

        Assert.Equal([new Person("ann", "Ann A", "1"), new Person("ben", null, "2")], people);
        Assert.Equal("/api/v1/friendships/12345/followers/?count=50&search_surface=follow_list_page", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Contains("max_id=50", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("sessionid=" + Session, handler.Requests[0].Headers.GetValues("Cookie").Single());
        Assert.Single(delays);
        Assert.InRange(delays[0].TotalSeconds, 2.5, 6.0);
    }

    [Fact]
    public async Task Redirect_to_login_means_the_session_expired()
    {
        var handler = new FakeHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://www.instagram.com/accounts/login/?next=/api/v1/");
            return r;
        });
        await Assert.ThrowsAsync<SessionExpiredException>(() => Client(handler).CheckSessionAsync());
    }

    [Theory]
    [InlineData("""{"message": "login_required", "status": "fail"}""", HttpStatusCode.Unauthorized)]
    [InlineData("""{"message": "checkpoint_required", "status": "fail"}""", HttpStatusCode.BadRequest)]
    public async Task Rejected_session_is_reported(string body, HttpStatusCode status)
    {
        var handler = new FakeHandler(Json(body, status));
        await Assert.ThrowsAsync<SessionExpiredException>(() => Client(handler).GetListAsync(FollowList.Following));
    }

    [Fact]
    public async Task Rate_limiting_stops_the_fetch()
    {
        var handler = new FakeHandler(Json("""{"message": "Please wait a few minutes before you try again.", "spam": true, "status": "fail"}""", HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<RateLimitedException>(() => Client(handler).GetListAsync(FollowList.Following));
    }

    [Fact]
    public async Task Fetch_refuses_a_truncated_list()
    {
        var handler = new FakeHandler(
            Json("""{"user": {"follower_count": 100, "following_count": 1}, "status": "ok"}"""),
            Json("""{"users": [{"pk": 1, "username": "ann"}], "status": "ok"}"""),
            Json("""{"users": [{"pk": 1, "username": "ann"}], "status": "ok"}"""));

        var e = await Assert.ThrowsAsync<IncompleteFetchException>(() => SnapshotFetcher.FetchAsync(Client(handler), null, NullLogger.Instance));
        Assert.Contains("profile says 100", e.Message);
    }

    [Fact]
    public async Task Fetch_builds_a_snapshot_with_expected_counts()
    {
        var handler = new FakeHandler(
            Json("""{"user": {"follower_count": 2, "following_count": 1}, "status": "ok"}"""),
            Json("""{"users": [{"pk": 1, "username": "ann"}, {"pk": 2, "username": "ben"}], "status": "ok"}"""),
            Json("""{"users": [{"pk": 2, "username": "ben"}], "status": "ok"}"""));

        var snap = await SnapshotFetcher.FetchAsync(Client(handler), null, NullLogger.Instance);

        Assert.Equal(SnapshotSource.Fetch, snap.Source);
        Assert.Equal(2, snap.Followers.Count);
        Assert.Equal(1, snap.FollowingExpected);
    }

    [Fact]
    public void Small_shortfalls_are_tolerated_but_an_empty_list_is_not()
    {
        SnapshotFetcher.Check("followers", got: 97, expected: 100, previousCount: 98); // deactivated accounts
        SnapshotFetcher.Check("followers", got: 2, expected: 5, previousCount: null); // within 3
        Assert.Throws<IncompleteFetchException>(() => SnapshotFetcher.Check("following", got: 0, expected: null, previousCount: 50));
    }
}
