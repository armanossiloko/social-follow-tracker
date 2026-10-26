using Microsoft.Extensions.Logging;

namespace ConnectionTracker.Core.Instagram;

public static class SnapshotFetcher
{
    public static async Task<Snapshot> FetchAsync(
        InstagramClient client, (int Followers, int Following)? previousCounts, ILogger log, CancellationToken ct = default)
    {
        var (followersExpected, followingExpected) = await client.GetProfileCountsAsync(ct);
        var takenAt = DateTimeOffset.UtcNow;
        log.LogInformation("Fetching followers...");
        var followers = await client.GetListAsync(FollowList.Followers, ct);
        log.LogInformation("Fetching following...");
        var following = await client.GetListAsync(FollowList.Following, ct);

        // A truncated list would show up as a wave of fake "unfollowed you" events, so refuse to save it.
        Check("followers", followers.Count, followersExpected, previousCounts?.Followers);
        Check("following", following.Count, followingExpected, previousCounts?.Following);

        return new Snapshot
        {
            TakenAt = takenAt,
            Source = SnapshotSource.Fetch,
            FollowersExpected = followersExpected,
            FollowingExpected = followingExpected,
            Followers = followers,
            Following = following,
        };
    }

    internal static void Check(string name, int got, int? expected, int? previousCount)
    {
        // Profile totals include deactivated/restricted accounts the list endpoint skips, so allow some slack.
        if (expected is int e && got < e * 0.9 && e - got > 3)
            throw new IncompleteFetchException($"Got {got} {name} but the profile says {e}; not saving this snapshot.");
        if (got == 0 && previousCount > 10)
            throw new IncompleteFetchException($"Got 0 {name} (previously {previousCount}); not saving this snapshot.");
    }
}
