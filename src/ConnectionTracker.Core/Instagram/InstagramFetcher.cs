using Microsoft.Extensions.Logging;

namespace ConnectionTracker.Core.Instagram;

public static class InstagramFetcher
{
    public static async Task<Snapshot> FetchAsync(
        InstagramClient client, ListCounts? previous, ILogger log, CancellationToken ct = default)
    {
        var (followersExpected, followingExpected) = await client.GetProfileCountsAsync(ct);
        var takenAt = DateTimeOffset.UtcNow;
        log.LogInformation("Fetching followers...");
        var followers = await client.GetListAsync(ConnectionList.Followers, ct);
        log.LogInformation("Fetching following...");
        var following = await client.GetListAsync(ConnectionList.Following, ct);

        FetchGuard.Check("followers", followers.Count, followersExpected, previous?.Followers);
        FetchGuard.Check("following", following.Count, followingExpected, previous?.Following);

        return new Snapshot
        {
            Platform = Platforms.Instagram,
            TakenAt = takenAt,
            Source = SnapshotSource.Fetch,
            FollowersExpected = followersExpected,
            FollowingExpected = followingExpected,
            Followers = followers,
            Following = following,
        };
    }
}
